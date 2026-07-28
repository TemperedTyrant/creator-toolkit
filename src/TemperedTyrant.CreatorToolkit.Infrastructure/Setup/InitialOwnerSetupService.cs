using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Setup;

public sealed class InitialOwnerSetupService(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public Task<InitialOwnerSetupResult> CreateAsync(
        InitialOwnerSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return coordinator.ExecuteAsync(
            operationToken => CreateWithinLockAsync(request, operationToken),
            cancellationToken);
    }

    private async Task<InitialOwnerSetupResult> CreateWithinLockAsync(
        InitialOwnerSetupRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        InstallationState installation = await dbContext.InstallationStates
            .SingleAsync(
                state => state.Id == InstallationState.SingletonId,
                cancellationToken);
        if (installation.InitializedAtUtc is not null)
        {
            return InitialOwnerSetupResult.AlreadyInitialized();
        }

        SecurityCapability? capability = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Purpose == CapabilityPurpose.BootstrapOwner
                    && candidate.ActiveSlot == SecurityCapability.BootstrapOwnerActiveSlot,
                cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        if (capability is null
            || capability.ExpiresAtUtc <= now
            || !MatchesCapability(request.Capability, capability.TokenHash))
        {
            return InitialOwnerSetupResult.InvalidCapability();
        }

        ApplicationUser user = ApplicationUser.CreateInitialOwner(
            request.UserName,
            request.DisplayName,
            now);
        IdentityResult createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            return InitialOwnerSetupResult.ValidationFailed(
                createResult.Errors.Select(ToSafeValidationMessage).Distinct().ToArray());
        }

        IdentityResult roleResult = await userManager.AddToRoleAsync(user, SystemRoles.Owner);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException("Initial Owner role assignment failed.");
        }

        Workspace workspace = Workspace.Create(now);
        dbContext.Workspaces.Add(workspace);
        dbContext.Ownerships.Add(
            Ownership.Create(Workspace.SingletonId, user.Id, now));
        installation.MarkInitialized(now);
        capability.Consume(now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.InitialOwnerCreated,
                AuditOutcome.Succeeded,
                ActorUserId: user.Id,
                TargetUserId: user.Id),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InitialOwnerSetupResult.Succeeded();
    }

    private static bool MatchesCapability(string? rawCapability, byte[] expectedHash)
    {
        const int encodedLength = 43;
        if (rawCapability?.Length != encodedLength)
        {
            return false;
        }

        byte[] decodedCapability;
        try
        {
            decodedCapability = WebEncoders.Base64UrlDecode(rawCapability);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decodedCapability.Length != 32
            || !string.Equals(
                WebEncoders.Base64UrlEncode(decodedCapability),
                rawCapability,
                StringComparison.Ordinal))
        {
            return false;
        }

        byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string ToSafeValidationMessage(IdentityError error)
    {
        return error.Code switch
        {
            "PasswordTooShort" or "PasswordTooLong" or "PasswordCommon"
                or "PasswordInvalidUnicode" or "PasswordRequiresUnique" => error.Description,
            "InvalidUserName" => "Use a valid local username.",
            "DuplicateUserName" => "That username is unavailable.",
            _ when error.Code.StartsWith("Password", StringComparison.Ordinal) =>
                "Choose a different password.",
            _ => "The account details are not valid.",
        };
    }
}

public sealed class InitialOwnerSetupRequest
{
    public InitialOwnerSetupRequest(
        string capability,
        string userName,
        string? displayName,
        string password)
    {
        Capability = capability;
        UserName = userName;
        DisplayName = displayName;
        Password = password;
    }

    public string Capability { get; }

    public string UserName { get; }

    public string? DisplayName { get; }

    public string Password { get; }

    public override string ToString() => nameof(InitialOwnerSetupRequest);
}

public sealed record InitialOwnerSetupResult(
    InitialOwnerSetupStatus Status,
    IReadOnlyList<string> ValidationErrors)
{
    public static InitialOwnerSetupResult Succeeded() =>
        new(InitialOwnerSetupStatus.Succeeded, []);

    public static InitialOwnerSetupResult AlreadyInitialized() =>
        new(InitialOwnerSetupStatus.AlreadyInitialized, []);

    public static InitialOwnerSetupResult InvalidCapability() =>
        new(InitialOwnerSetupStatus.InvalidCapability, []);

    public static InitialOwnerSetupResult ValidationFailed(IReadOnlyList<string> errors) =>
        new(InitialOwnerSetupStatus.ValidationFailed, errors);
}

public enum InitialOwnerSetupStatus
{
    Succeeded = 1,
    AlreadyInitialized = 2,
    InvalidCapability = 3,
    ValidationFailed = 4,
}
