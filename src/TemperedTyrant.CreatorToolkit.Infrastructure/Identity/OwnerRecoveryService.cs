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

public sealed class OwnerRecoveryIssuer(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(30);

    public Task IssueAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
        {
            throw new ArgumentException("A capability hash must contain 32 bytes.", nameof(tokenHash));
        }

        return coordinator.ExecuteAsync(
            token => IssueWithinLockAsync(tokenHash, token),
            cancellationToken);
    }

    private async Task IssueWithinLockAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Ownership ownership = await dbContext.Ownerships.SingleAsync(cancellationToken);
        ApplicationUser owner = await userManager.FindByIdAsync(ownership.OwnerUserId.ToString())
            ?? throw new InvalidOperationException("The current Owner account is unavailable.");
        if (owner.ActivatedAtUtc is null
            || !await userManager.IsInRoleAsync(owner, SystemRoles.Owner))
        {
            throw new InvalidOperationException("The current Owner account is invalid.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        SecurityCapability? previous = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                capability =>
                    capability.Purpose == CapabilityPurpose.RecoverOwner
                    && capability.ActiveSlot == SecurityCapability.RecoverOwnerActiveSlot,
                cancellationToken);
        if (previous is not null)
        {
            previous.Revoke(now);
        }

        dbContext.SecurityCapabilities.Add(
            SecurityCapability.CreateOwnerRecovery(
                tokenHash,
                owner.Id,
                now,
                now.Add(CapabilityLifetime)));
        IdentityResult stampResult = await userManager.UpdateSecurityStampAsync(owner);
        EnsureSucceeded(stampResult, "Owner session invalidation failed.");
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.OwnerRecoveryCapabilityCreated,
                AuditOutcome.Succeeded,
                TargetUserId: owner.Id,
                ReasonCode: previous is null ? null : AuditReasonCode.Replaced),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed class OwnerRecoveryService(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public Task<OwnerRecoveryResult> CompleteAsync(
        string? rawCapability,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => CompleteWithinLockAsync(rawCapability, newPassword, token),
            cancellationToken);
    }

    private async Task<OwnerRecoveryResult> CompleteWithinLockAsync(
        string? rawCapability,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (!TryHashCanonicalCapability(rawCapability, out byte[] submittedHash))
        {
            return OwnerRecoveryResult.Invalid();
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        SecurityCapability? capability = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Purpose == CapabilityPurpose.RecoverOwner
                    && candidate.TokenHash == submittedHash
                    && candidate.ActiveSlot == SecurityCapability.RecoverOwnerActiveSlot,
                cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        if (capability?.SubjectUserId is not Guid subjectUserId
            || capability.ExpiresAtUtc <= now
            || !CryptographicOperations.FixedTimeEquals(
                submittedHash,
                capability.TokenHash))
        {
            return OwnerRecoveryResult.Invalid();
        }

        Ownership ownership = await dbContext.Ownerships.SingleAsync(cancellationToken);
        if (ownership.OwnerUserId != subjectUserId)
        {
            return OwnerRecoveryResult.Invalid();
        }

        ApplicationUser? owner = await userManager.FindByIdAsync(subjectUserId.ToString());
        if (owner?.ActivatedAtUtc is null
            || !await userManager.IsInRoleAsync(owner, SystemRoles.Owner))
        {
            return OwnerRecoveryResult.Invalid();
        }

        string identityResetToken = await userManager.GeneratePasswordResetTokenAsync(owner);
        IdentityResult resetResult = await userManager.ResetPasswordAsync(
            owner,
            identityResetToken,
            newPassword);
        if (!resetResult.Succeeded)
        {
            return OwnerRecoveryResult.ValidationFailed(
                resetResult.Errors
                    .Select(ToSafePasswordMessage)
                    .Distinct()
                    .ToArray());
        }

        owner.IsEnabled = true;
        IdentityResult ownerUpdate = await userManager.UpdateAsync(owner);
        EnsureSucceeded(ownerUpdate, "Owner recovery update failed.");
        capability.Consume(now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.OwnerRecovered,
                AuditOutcome.Succeeded,
                TargetUserId: owner.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OwnerRecoveryResult.Succeeded();
    }

    private static bool TryHashCanonicalCapability(
        string? rawCapability,
        out byte[] capabilityHash)
    {
        capabilityHash = [];
        if (rawCapability?.Length != 43)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = WebEncoders.Base64UrlDecode(rawCapability);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length != 32
            || !string.Equals(
                WebEncoders.Base64UrlEncode(decoded),
                rawCapability,
                StringComparison.Ordinal))
        {
            return false;
        }

        capabilityHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability));
        return true;
    }

    private static string ToSafePasswordMessage(IdentityError error)
    {
        return error.Code switch
        {
            "PasswordTooShort" or "PasswordTooLong" or "PasswordCommon"
                or "PasswordInvalidUnicode" or "PasswordRequiresUnique" => error.Description,
            _ => "Choose a different password.",
        };
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed record OwnerRecoveryResult(
    OwnerRecoveryStatus Status,
    IReadOnlyList<string> ValidationErrors)
{
    public static OwnerRecoveryResult Succeeded() =>
        new(OwnerRecoveryStatus.Succeeded, []);

    public static OwnerRecoveryResult Invalid() =>
        new(OwnerRecoveryStatus.Invalid, []);

    public static OwnerRecoveryResult ValidationFailed(IReadOnlyList<string> errors) =>
        new(OwnerRecoveryStatus.ValidationFailed, errors);
}

public enum OwnerRecoveryStatus
{
    Succeeded = 1,
    Invalid = 2,
    ValidationFailed = 3,
}
