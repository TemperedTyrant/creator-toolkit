using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Security;

internal sealed class DataProtectionSecretStore(
    CreatorToolkitDbContext context,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : ISecretStore
{
    private const string ProtectorPurpose =
        "TemperedTyrant.CreatorToolkit.ProtectedSecretStore.v1";

    public async Task<SecretReference> CreateAsync(
        string purpose,
        string value,
        CancellationToken cancellationToken = default)
    {
        ValidatePurpose(purpose);
        ArgumentException.ThrowIfNullOrEmpty(value);

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProtectedSecretRecord record = new()
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            Ciphertext = CreateProtector(purpose).Protect(value),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        context.ProtectedSecrets.Add(record);
        await context.SaveChangesAsync(cancellationToken);
        return new SecretReference(record.Id);
    }

    public async Task ReplaceAsync(
        SecretReference secret,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        ProtectedSecretRecord record = await context.ProtectedSecrets
            .SingleOrDefaultAsync(candidate => candidate.Id == secret.Id, cancellationToken)
            ?? throw new KeyNotFoundException("The protected secret does not exist.");
        await context.Entry(record).ReloadAsync(cancellationToken);

        record.Ciphertext = CreateProtector(record.Purpose).Protect(value);
        record.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        SecretReference secret,
        CancellationToken cancellationToken = default)
    {
        ProtectedSecretRecord? record = await context.ProtectedSecrets
            .SingleOrDefaultAsync(candidate => candidate.Id == secret.Id, cancellationToken);

        if (record is null)
        {
            return false;
        }

        await context.Entry(record).ReloadAsync(cancellationToken);

        context.ProtectedSecrets.Remove(record);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void ValidatePurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (purpose.Length > 128)
        {
            throw new ArgumentException(
                "The protected-secret purpose cannot exceed 128 characters.",
                nameof(purpose));
        }

        if (purpose.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not ':' and not '-'))
        {
            throw new ArgumentException(
                "The protected-secret purpose must be a non-secret identifier.",
                nameof(purpose));
        }
    }

    private IDataProtector CreateProtector(string purpose)
    {
        return dataProtectionProvider.CreateProtector(ProtectorPurpose, purpose);
    }
}
