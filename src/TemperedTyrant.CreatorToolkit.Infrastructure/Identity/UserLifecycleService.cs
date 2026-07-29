using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class UserLifecycleService(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan ActivationLifetime = TimeSpan.FromHours(24);

    public Task<UserLifecycleResult> CreatePendingAsync(
        Guid actorUserId,
        string userName,
        string? displayName,
        string role,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => CreatePendingWithinLockAsync(
                actorUserId,
                userName,
                displayName,
                role,
                token),
            cancellationToken);
    }

    public Task<UserLifecycleResult> RegenerateActivationAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => RegenerateActivationWithinLockAsync(
                actorUserId,
                targetUserId,
                expectedConcurrencyStamp,
                token),
            cancellationToken);
    }

    public Task<UserLifecycleResult> ChangeRoleAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        string newRole,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => ChangeRoleWithinLockAsync(
                actorUserId,
                targetUserId,
                expectedConcurrencyStamp,
                newRole,
                token),
            cancellationToken);
    }

    public Task<UserLifecycleResult> DisableAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => DisableWithinLockAsync(
                actorUserId,
                targetUserId,
                expectedConcurrencyStamp,
                token),
            cancellationToken);
    }

    public Task<UserLifecycleResult> DeleteAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => DeleteWithinLockAsync(
                actorUserId,
                targetUserId,
                expectedConcurrencyStamp,
                token),
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetCreatableRolesAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? actor = await userManager.FindByIdAsync(actorUserId.ToString());
        string? actorRole = await GetActiveManagerRoleAsync(actor);
        return actorRole switch
        {
            SystemRoles.Owner => [SystemRoles.Admin, SystemRoles.Editor, SystemRoles.Viewer],
            SystemRoles.Admin => [SystemRoles.Editor, SystemRoles.Viewer],
            _ => [],
        };
    }

    public async Task<UserDirectoryResult?> GetUserDirectoryAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? actor = await userManager.FindByIdAsync(actorUserId.ToString());
        string? actorRole = await GetActiveManagerRoleAsync(actor);
        if (actorRole is null)
        {
            return null;
        }

        Guid ownerUserId = await dbContext.Ownerships
            .AsNoTracking()
            .Select(ownership => ownership.OwnerUserId)
            .SingleAsync(cancellationToken);
        ApplicationUser[] users = await userManager.Users
            .AsNoTracking()
            .OrderBy(user => user.NormalizedUserName)
            .ToArrayAsync(cancellationToken);
        List<UserDirectoryEntry> entries = new(users.Length);
        foreach (ApplicationUser user in users)
        {
            string? role = await GetSingleRoleAsync(user);
            if (role is null)
            {
                continue;
            }

            UserAccountState state = user.ActivatedAtUtc is null
                ? UserAccountState.Pending
                : user.IsEnabled
                    ? UserAccountState.Active
                    : UserAccountState.Disabled;
            bool canManage = user.Id != ownerUserId
                && user.Id != actorUserId
                && CanManage(actorRole, role, role);
            entries.Add(
                new UserDirectoryEntry(
                    user.Id,
                    user.UserName!,
                    role,
                    state,
                    canManage));
        }

        return new UserDirectoryResult(
            actorRole,
            entries,
            actorRole == SystemRoles.Owner);
    }

    public async Task<ManageableUser?> GetManageableUserAsync(
        Guid actorUserId,
        Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? actor = await userManager.FindByIdAsync(actorUserId.ToString());
        string? actorRole = await GetActiveManagerRoleAsync(actor);
        ApplicationUser? target = await userManager.FindByIdAsync(targetUserId.ToString());
        if (actorRole is null || target is null)
        {
            return null;
        }

        Ownership ownership = await dbContext.Ownerships
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (ownership.OwnerUserId == target.Id)
        {
            return null;
        }

        string? targetRole = await GetSingleRoleAsync(target);
        if (targetRole is null || !CanManage(actorRole, targetRole, targetRole))
        {
            return null;
        }

        IReadOnlyList<string> assignableRoles = actorRole switch
        {
            SystemRoles.Owner => [SystemRoles.Admin, SystemRoles.Editor, SystemRoles.Viewer],
            SystemRoles.Admin => [SystemRoles.Editor, SystemRoles.Viewer],
            _ => [],
        };
        return new ManageableUser(
            target.Id,
            target.UserName!,
            target.DisplayName,
            targetRole,
            target.IsEnabled,
            target.ActivatedAtUtc is not null,
            target.ConcurrencyStamp!,
            assignableRoles);
    }

    private async Task<UserLifecycleResult> CreatePendingWithinLockAsync(
        Guid actorUserId,
        string userName,
        string? displayName,
        string role,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ApplicationUser? actor = await userManager.FindByIdAsync(actorUserId.ToString());
        string? actorRole = await GetActiveManagerRoleAsync(actor);
        if (!CanCreate(actorRole, role))
        {
            return UserLifecycleResult.Forbidden();
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        ApplicationUser target = ApplicationUser.CreatePending(userName, displayName, now);
        IdentityResult createResult = await userManager.CreateAsync(target);
        if (!createResult.Succeeded)
        {
            return UserLifecycleResult.ValidationFailed(ToSafeErrors(createResult));
        }

        IdentityResult roleResult = await userManager.AddToRoleAsync(target, role);
        EnsureIdentitySucceeded(roleResult, "Pending account role assignment failed.");

        IssuedCapability issued = await CreateUniqueActivationAsync(
            target.Id,
            actorUserId,
            now,
            cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.PendingUserCreated,
                AuditOutcome.Succeeded,
                actorUserId,
                target.Id),
            cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.ActivationCapabilityCreated,
                AuditOutcome.Succeeded,
                actorUserId,
                target.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return UserLifecycleResult.Succeeded(target.Id, target.ConcurrencyStamp, issued.Raw);
    }

    private async Task<UserLifecycleResult> RegenerateActivationWithinLockAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ManagementContext? context = await GetManagementContextAsync(
            actorUserId,
            targetUserId,
            expectedConcurrencyStamp);
        if (context?.Status is not null)
        {
            return context.Status;
        }

        if (context!.Target.ActivatedAtUtc is not null)
        {
            return UserLifecycleResult.InvalidState();
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        SecurityCapability? previous = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                capability =>
                    capability.Purpose == CapabilityPurpose.ActivateUser
                    && capability.ActiveSlot == $"activate:{targetUserId:N}",
                cancellationToken);
        if (previous is not null)
        {
            previous.Revoke(now);
        }

        IssuedCapability issued = await CreateUniqueActivationAsync(
            targetUserId,
            actorUserId,
            now,
            cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.ActivationCapabilityCreated,
                AuditOutcome.Succeeded,
                actorUserId,
                targetUserId,
                previous is null ? null : AuditReasonCode.Replaced),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return UserLifecycleResult.Succeeded(
            targetUserId,
            context.Target.ConcurrencyStamp,
            issued.Raw);
    }

    private async Task<UserLifecycleResult> ChangeRoleWithinLockAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        string newRole,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ManagementContext? context = await GetManagementContextAsync(
            actorUserId,
            targetUserId,
            expectedConcurrencyStamp);
        if (context?.Status is not null)
        {
            return context.Status;
        }

        if (!CanManage(context!.ActorRole, context.TargetRole, newRole))
        {
            return UserLifecycleResult.Forbidden();
        }

        if (string.Equals(context.TargetRole, newRole, StringComparison.Ordinal))
        {
            return UserLifecycleResult.Succeeded(
                context.Target.Id,
                context.Target.ConcurrencyStamp);
        }

        IdentityResult removeResult =
            await userManager.RemoveFromRoleAsync(context.Target, context.TargetRole);
        EnsureIdentitySucceeded(removeResult, "Existing account role removal failed.");
        IdentityResult addResult = await userManager.AddToRoleAsync(context.Target, newRole);
        EnsureIdentitySucceeded(addResult, "New account role assignment failed.");
        IdentityResult stampResult = await userManager.UpdateSecurityStampAsync(context.Target);
        EnsureIdentitySucceeded(stampResult, "Account session invalidation failed.");

        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserRoleChanged,
                AuditOutcome.Succeeded,
                actorUserId,
                targetUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return UserLifecycleResult.Succeeded(
            context.Target.Id,
            context.Target.ConcurrencyStamp);
    }

    private async Task<UserLifecycleResult> DisableWithinLockAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ManagementContext? context = await GetManagementContextAsync(
            actorUserId,
            targetUserId,
            expectedConcurrencyStamp);
        if (context?.Status is not null)
        {
            return context.Status;
        }

        if (!CanManage(context!.ActorRole, context.TargetRole, context.TargetRole))
        {
            return UserLifecycleResult.Forbidden();
        }

        if (!context.Target.IsEnabled || context.Target.ActivatedAtUtc is null)
        {
            return UserLifecycleResult.InvalidState();
        }

        context.Target.IsEnabled = false;
        IdentityResult stampResult = await userManager.UpdateSecurityStampAsync(context.Target);
        EnsureIdentitySucceeded(stampResult, "Account session invalidation failed.");
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserDisabled,
                AuditOutcome.Succeeded,
                actorUserId,
                targetUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return UserLifecycleResult.Succeeded(
            context.Target.Id,
            context.Target.ConcurrencyStamp);
    }

    private async Task<UserLifecycleResult> DeleteWithinLockAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ManagementContext? context = await GetManagementContextAsync(
            actorUserId,
            targetUserId,
            expectedConcurrencyStamp);
        if (context?.Status is not null)
        {
            return context.Status;
        }

        if (!CanManage(context!.ActorRole, context.TargetRole, context.TargetRole))
        {
            return UserLifecycleResult.Forbidden();
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        SecurityCapability[] activeCapabilities = await dbContext.SecurityCapabilities
            .Where(
                capability =>
                    capability.SubjectUserId == targetUserId
                    && capability.ActiveSlot != null)
            .ToArrayAsync(cancellationToken);
        foreach (SecurityCapability capability in activeCapabilities)
        {
            capability.Revoke(now);
        }

        IdentityResult deleteResult = await userManager.DeleteAsync(context.Target);
        EnsureIdentitySucceeded(deleteResult, "Account deletion failed.");
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserDeleted,
                AuditOutcome.Succeeded,
                actorUserId,
                targetUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return UserLifecycleResult.Succeeded(targetUserId, null);
    }

    private async Task<ManagementContext?> GetManagementContextAsync(
        Guid actorUserId,
        Guid targetUserId,
        string expectedConcurrencyStamp)
    {
        ApplicationUser? actor = await userManager.FindByIdAsync(actorUserId.ToString());
        string? actorRole = await GetActiveManagerRoleAsync(actor);
        if (actorRole is null)
        {
            return new(null!, string.Empty, string.Empty, UserLifecycleResult.Forbidden());
        }

        ApplicationUser? target = await userManager.FindByIdAsync(targetUserId.ToString());
        if (target is null)
        {
            return new(null!, actorRole, string.Empty, UserLifecycleResult.NotFound());
        }

        if (!string.Equals(
                target.ConcurrencyStamp,
                expectedConcurrencyStamp,
                StringComparison.Ordinal))
        {
            return new(target, actorRole, string.Empty, UserLifecycleResult.Conflict());
        }

        Ownership ownership = await dbContext.Ownerships.SingleAsync();
        if (ownership.OwnerUserId == target.Id)
        {
            return new(
                target,
                actorRole,
                SystemRoles.Owner,
                actorRole == SystemRoles.Owner
                    ? UserLifecycleResult.SoleOwnerProtected()
                    : UserLifecycleResult.Forbidden());
        }

        string? targetRole = await GetSingleRoleAsync(target);
        if (targetRole is null)
        {
            return new(target, actorRole, string.Empty, UserLifecycleResult.InvalidState());
        }

        return new(target, actorRole, targetRole, null);
    }

    private async Task<string?> GetActiveManagerRoleAsync(ApplicationUser? user)
    {
        if (user?.IsEnabled != true || user.ActivatedAtUtc is null)
        {
            return null;
        }

        string? role = await GetSingleRoleAsync(user);
        return role is SystemRoles.Owner or SystemRoles.Admin ? role : null;
    }

    private async Task<string?> GetSingleRoleAsync(ApplicationUser user)
    {
        IList<string> roles = await userManager.GetRolesAsync(user);
        return roles.Count == 1 ? roles[0] : null;
    }

    private async Task<IssuedCapability> CreateUniqueActivationAsync(
        Guid subjectUserId,
        Guid createdByUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            if (!await dbContext.SecurityCapabilities.AnyAsync(
                    capability => capability.TokenHash == hash,
                    cancellationToken))
            {
                dbContext.SecurityCapabilities.Add(
                    SecurityCapability.CreateActivation(
                        hash,
                        subjectUserId,
                        createdByUserId,
                        now,
                        now.Add(ActivationLifetime)));
                return new(raw);
            }
        }

        throw new InvalidOperationException("Activation capability generation failed.");
    }

    private static bool CanCreate(string? actorRole, string role)
    {
        return actorRole switch
        {
            SystemRoles.Owner => role is SystemRoles.Admin or SystemRoles.Editor or SystemRoles.Viewer,
            SystemRoles.Admin => role is SystemRoles.Editor or SystemRoles.Viewer,
            _ => false,
        };
    }

    private static bool CanManage(string actorRole, string targetRole, string newRole)
    {
        return actorRole switch
        {
            SystemRoles.Owner =>
                targetRole is SystemRoles.Admin or SystemRoles.Editor or SystemRoles.Viewer
                && newRole is SystemRoles.Admin or SystemRoles.Editor or SystemRoles.Viewer,
            SystemRoles.Admin =>
                targetRole is SystemRoles.Editor or SystemRoles.Viewer
                && newRole is SystemRoles.Editor or SystemRoles.Viewer,
            _ => false,
        };
    }

    private static string[] ToSafeErrors(IdentityResult result)
    {
        return result.Errors
            .Select(
                error => error.Code switch
                {
                    "InvalidUserName" => "Use a valid local username.",
                    "DuplicateUserName" => "That username is unavailable.",
                    _ => "The account details are not valid.",
                })
            .Distinct()
            .ToArray();
    }

    private static void EnsureIdentitySucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ManagementContext(
        ApplicationUser Target,
        string ActorRole,
        string TargetRole,
        UserLifecycleResult? Status);

    private sealed record IssuedCapability(string Raw);
}

public sealed record UserLifecycleResult(
    UserLifecycleStatus Status,
    Guid? TargetUserId,
    string? ConcurrencyStamp,
    string? OneTimeActivationCapability,
    IReadOnlyList<string> ValidationErrors)
{
    public static UserLifecycleResult Succeeded(
        Guid targetUserId,
        string? concurrencyStamp,
        string? capability = null) =>
        new(
            UserLifecycleStatus.Succeeded,
            targetUserId,
            concurrencyStamp,
            capability,
            []);

    public static UserLifecycleResult Forbidden() => Of(UserLifecycleStatus.Forbidden);

    public static UserLifecycleResult NotFound() => Of(UserLifecycleStatus.NotFound);

    public static UserLifecycleResult Conflict() => Of(UserLifecycleStatus.Conflict);

    public static UserLifecycleResult InvalidState() => Of(UserLifecycleStatus.InvalidState);

    public static UserLifecycleResult SoleOwnerProtected() =>
        Of(UserLifecycleStatus.SoleOwnerProtected);

    public static UserLifecycleResult ValidationFailed(IReadOnlyList<string> errors) =>
        new(UserLifecycleStatus.ValidationFailed, null, null, null, errors);

    public override string ToString() => nameof(UserLifecycleResult);

    private static UserLifecycleResult Of(UserLifecycleStatus status) =>
        new(status, null, null, null, []);
}

public enum UserLifecycleStatus
{
    Succeeded = 1,
    Forbidden = 2,
    NotFound = 3,
    Conflict = 4,
    InvalidState = 5,
    SoleOwnerProtected = 6,
    ValidationFailed = 7,
}

public sealed record ManageableUser(
    Guid UserId,
    string UserName,
    string DisplayName,
    string Role,
    bool IsEnabled,
    bool IsActivated,
    string ConcurrencyStamp,
    IReadOnlyList<string> AssignableRoles);

public sealed record UserDirectoryResult(
    string ActorRole,
    IReadOnlyList<UserDirectoryEntry> Users,
    bool CanTransferOwnership);

public sealed record UserDirectoryEntry(
    Guid UserId,
    string UserName,
    string Role,
    UserAccountState State,
    bool CanManage);

public enum UserAccountState
{
    Pending = 1,
    Active = 2,
    Disabled = 3,
}
