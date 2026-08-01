using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Security;

internal interface IProtectedSecretValueResolver
{
    Task<T> UseAsync<T>(
        SecretReference reference,
        string expectedPurpose,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}

internal sealed class ProtectedSecretValueResolver(
    IDbContextFactory<CreatorToolkitDbContext> contextFactory,
    IDataProtectionProvider dataProtectionProvider) : IProtectedSecretValueResolver
{
    private const string ProtectorPurpose =
        "TemperedTyrant.CreatorToolkit.ProtectedSecretStore.v1";

    public async Task<T> UseAsync<T>(
        SecretReference reference,
        string expectedPurpose,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPurpose);
        ArgumentNullException.ThrowIfNull(operation);

        await using CreatorToolkitDbContext context =
            await contextFactory.CreateDbContextAsync(cancellationToken);
        ProtectedSecretRecord record = await context.ProtectedSecrets
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == reference.Id, cancellationToken)
            ?? throw new Discord.DiscordApiAuthenticationException();
        if (!string.Equals(record.Purpose, expectedPurpose, StringComparison.Ordinal))
        {
            throw new Discord.DiscordApiAuthenticationException();
        }

        string value;
        try
        {
            value = dataProtectionProvider
                .CreateProtector(ProtectorPurpose, expectedPurpose)
                .Unprotect(record.Ciphertext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new Discord.DiscordApiAuthenticationException();
        }

        return await operation(value, cancellationToken);
    }
}
