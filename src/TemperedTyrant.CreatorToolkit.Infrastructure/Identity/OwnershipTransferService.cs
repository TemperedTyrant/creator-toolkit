using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class OwnershipTransferService(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public Task<OwnershipTransferResult> TransferAsync(
        Guid actorUserId,
        Guid targetUserId,
        string currentPassword,
        long expectedOwnershipRevision,
        string expectedTargetConcurrencyStamp,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => TransferWithinLockAsync(
                actorUserId,
                targetUserId,
                currentPassword,
                expectedOwnershipRevision,
                expectedTargetConcurrencyStamp,
                token),
            cancellationToken);
    }

    public async Task<IReadOnlyList<OwnershipTarget>> GetEligibleTargetsAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        Ownership ownership = await dbContext.Ownerships
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (ownership.OwnerUserId != actorUserId)
        {
            return [];
        }

        ApplicationUser? owner = await userManager.FindByIdAsync(actorUserId.ToString());
        if (owner?.IsEnabled != true
            || owner.ActivatedAtUtc is null
            || !await userManager.IsInRoleAsync(owner, SystemRoles.Owner))
        {
            return [];
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(
                user =>
                    user.Id != actorUserId
                    && user.IsEnabled
                    && user.ActivatedAtUtc != null)
            .OrderBy(user => user.UserName)
            .Select(
                user => new OwnershipTarget(
                    user.Id,
                    user.UserName!,
                    user.DisplayName,
                    user.ConcurrencyStamp!))
            .ToArrayAsync(cancellationToken);
    }

    private async Task<OwnershipTransferResult> TransferWithinLockAsync(
        Guid actorUserId,
        Guid targetUserId,
        string currentPassword,
        long expectedOwnershipRevision,
        string expectedTargetConcurrencyStamp,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Ownership ownership = await dbContext.Ownerships.SingleAsync(cancellationToken);
        if (ownership.OwnerUserId != actorUserId)
        {
            return OwnershipTransferResult.Forbidden();
        }

        if (ownership.Revision != expectedOwnershipRevision)
        {
            return OwnershipTransferResult.Conflict();
        }

        ApplicationUser? owner = await userManager.FindByIdAsync(actorUserId.ToString());
        if (owner?.IsEnabled != true
            || owner.ActivatedAtUtc is null
            || !await userManager.IsInRoleAsync(owner, SystemRoles.Owner))
        {
            return OwnershipTransferResult.Forbidden();
        }

        bool isLockedOut = await userManager.IsLockedOutAsync(owner);
        bool passwordAccepted = !isLockedOut
            && await userManager.CheckPasswordAsync(owner, currentPassword);
        if (!passwordAccepted)
        {
            if (!isLockedOut)
            {
                IdentityResult accessFailure = await userManager.AccessFailedAsync(owner);
                EnsureSucceeded(accessFailure, "Ownership reauthentication state update failed.");
                isLockedOut = await userManager.IsLockedOutAsync(owner);
            }

            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.OwnershipTransferRejected,
                    AuditOutcome.Rejected,
                    actorUserId,
                    TargetUserId: targetUserId,
                    ReasonCode: isLockedOut
                        ? AuditReasonCode.LockedOut
                        : AuditReasonCode.InvalidCredentials),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OwnershipTransferResult.InvalidPassword();
        }

        if (await userManager.GetAccessFailedCountAsync(owner) > 0)
        {
            IdentityResult resetFailures = await userManager.ResetAccessFailedCountAsync(owner);
            EnsureSucceeded(resetFailures, "Ownership reauthentication state reset failed.");
        }

        ApplicationUser? target = await userManager.FindByIdAsync(targetUserId.ToString());
        if (target?.IsEnabled != true
            || target.ActivatedAtUtc is null
            || !string.Equals(
                target.ConcurrencyStamp,
                expectedTargetConcurrencyStamp,
                StringComparison.Ordinal))
        {
            return target is null
                ? OwnershipTransferResult.InvalidTarget()
                : OwnershipTransferResult.Conflict();
        }

        IList<string> targetRoles = await userManager.GetRolesAsync(target);
        if (targetRoles.Count != 1
            || targetRoles[0] is not (
                SystemRoles.Admin or SystemRoles.Editor or SystemRoles.Viewer))
        {
            return OwnershipTransferResult.InvalidTarget();
        }

        IdentityResult targetRoleRemoval =
            await userManager.RemoveFromRoleAsync(target, targetRoles[0]);
        EnsureSucceeded(targetRoleRemoval, "Target role removal failed.");
        IdentityResult targetPromotion =
            await userManager.AddToRoleAsync(target, SystemRoles.Owner);
        EnsureSucceeded(targetPromotion, "Target Owner promotion failed.");
        IdentityResult ownerRoleRemoval =
            await userManager.RemoveFromRoleAsync(owner, SystemRoles.Owner);
        EnsureSucceeded(ownerRoleRemoval, "Former Owner role removal failed.");
        IdentityResult ownerDemotion =
            await userManager.AddToRoleAsync(owner, SystemRoles.Admin);
        EnsureSucceeded(ownerDemotion, "Former Owner demotion failed.");

        ownership.TransferTo(target.Id, timeProvider.GetUtcNow().ToUniversalTime());

        IdentityResult targetStamp = await userManager.UpdateSecurityStampAsync(target);
        EnsureSucceeded(targetStamp, "Target session invalidation failed.");
        IdentityResult ownerStamp = await userManager.UpdateSecurityStampAsync(owner);
        EnsureSucceeded(ownerStamp, "Former Owner session invalidation failed.");

        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserRoleChanged,
                AuditOutcome.Succeeded,
                actorUserId,
                target.Id),
            cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserRoleChanged,
                AuditOutcome.Succeeded,
                actorUserId,
                owner.Id),
            cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.OwnershipTransferred,
                AuditOutcome.Succeeded,
                actorUserId,
                target.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OwnershipTransferResult.Succeeded();
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed record OwnershipTarget(
    Guid UserId,
    string UserName,
    string DisplayName,
    string ConcurrencyStamp);

public sealed record OwnershipTransferResult(OwnershipTransferStatus Status)
{
    public static OwnershipTransferResult Succeeded() =>
        new(OwnershipTransferStatus.Succeeded);

    public static OwnershipTransferResult Forbidden() =>
        new(OwnershipTransferStatus.Forbidden);

    public static OwnershipTransferResult Conflict() =>
        new(OwnershipTransferStatus.Conflict);

    public static OwnershipTransferResult InvalidPassword() =>
        new(OwnershipTransferStatus.InvalidPassword);

    public static OwnershipTransferResult InvalidTarget() =>
        new(OwnershipTransferStatus.InvalidTarget);
}

public enum OwnershipTransferStatus
{
    Succeeded = 1,
    Forbidden = 2,
    Conflict = 3,
    InvalidPassword = 4,
    InvalidTarget = 5,
}
