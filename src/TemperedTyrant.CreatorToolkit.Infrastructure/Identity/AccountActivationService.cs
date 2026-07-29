using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class AccountActivationService(
    CreatorToolkitDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public Task<AccountActivationResult> ActivateAsync(
        string? rawCapability,
        string password,
        CancellationToken cancellationToken = default)
    {
        return coordinator.ExecuteAsync(
            token => ActivateWithinLockAsync(rawCapability, password, token),
            cancellationToken);
    }

    private async Task<AccountActivationResult> ActivateWithinLockAsync(
        string? rawCapability,
        string password,
        CancellationToken cancellationToken)
    {
        if (!TryHashCanonicalCapability(rawCapability, out byte[] submittedHash))
        {
            return AccountActivationResult.Invalid();
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        SecurityCapability? capability = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Purpose == CapabilityPurpose.ActivateUser
                    && candidate.TokenHash == submittedHash
                    && candidate.ActiveSlot != null,
                cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        if (capability?.SubjectUserId is not Guid subjectUserId
            || capability.ExpiresAtUtc <= now
            || !CryptographicOperations.FixedTimeEquals(
                submittedHash,
                capability.TokenHash))
        {
            return AccountActivationResult.Invalid();
        }

        ApplicationUser? user = await userManager.FindByIdAsync(subjectUserId.ToString());
        IList<string>? roles = user is null ? null : await userManager.GetRolesAsync(user);
        if (user is null
            || user.ActivatedAtUtc is not null
            || user.IsEnabled
            || roles?.Count != 1
            || roles[0] is not (
                SystemRoles.Admin or SystemRoles.Editor or SystemRoles.Viewer))
        {
            return AccountActivationResult.Invalid();
        }

        IdentityResult passwordResult = await userManager.AddPasswordAsync(user, password);
        if (!passwordResult.Succeeded)
        {
            return AccountActivationResult.ValidationFailed(
                passwordResult.Errors
                    .Select(ToSafePasswordMessage)
                    .Distinct()
                    .ToArray());
        }

        user.Activate(now);
        IdentityResult updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException("Account activation update failed.");
        }

        capability.Consume(now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.UserActivated,
                AuditOutcome.Succeeded,
                TargetUserId: user.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountActivationResult.Succeeded();
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
}

public sealed record AccountActivationResult(
    AccountActivationStatus Status,
    IReadOnlyList<string> ValidationErrors)
{
    public static AccountActivationResult Succeeded() =>
        new(AccountActivationStatus.Succeeded, []);

    public static AccountActivationResult Invalid() =>
        new(AccountActivationStatus.Invalid, []);

    public static AccountActivationResult ValidationFailed(IReadOnlyList<string> errors) =>
        new(AccountActivationStatus.ValidationFailed, errors);
}

public enum AccountActivationStatus
{
    Succeeded = 1,
    Invalid = 2,
    ValidationFailed = 3,
}
