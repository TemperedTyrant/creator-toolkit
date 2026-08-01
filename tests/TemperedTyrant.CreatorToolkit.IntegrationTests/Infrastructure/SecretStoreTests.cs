using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task CreateAndReplacePersistOnlyProtectedValues()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ISecretStore store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        const string original = "original-sensitive-value-7fd518a2";
        const string replacement = "replacement-sensitive-value-b110750c";

        SecretReference reference = await store.CreateAsync("integration-test", original);
        SecretRow firstRow = await ReadSecretRowAsync(provider, reference);
        Assert.DoesNotContain(original, firstRow.Ciphertext, StringComparison.Ordinal);
        Assert.Equal("integration-test", firstRow.Purpose);
        Assert.Equal(0, firstRow.Revision);

        IDataProtectionProvider protectionProvider =
            provider.GetRequiredService<IDataProtectionProvider>();
        IDataProtector correctProtector = protectionProvider.CreateProtector(
            "TemperedTyrant.CreatorToolkit.ProtectedSecretStore.v1",
            firstRow.Purpose);
        IDataProtector wrongProtector = protectionProvider.CreateProtector(
            "TemperedTyrant.CreatorToolkit.ProtectedSecretStore.v1",
            "different-purpose");
        Assert.Equal(original, correctProtector.Unprotect(firstRow.Ciphertext));
        Assert.Throws<CryptographicException>(
            () => wrongProtector.Unprotect(firstRow.Ciphertext));

        await store.ReplaceAsync(reference, replacement);
        SecretRow secondRow = await ReadSecretRowAsync(provider, reference);
        Assert.Equal(firstRow.Purpose, secondRow.Purpose);
        Assert.Equal(1, secondRow.Revision);
        Assert.NotEqual(firstRow.Ciphertext, secondRow.Ciphertext);
        Assert.DoesNotContain(original, secondRow.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement, secondRow.Ciphertext, StringComparison.Ordinal);
        Assert.Equal(replacement, correctProtector.Unprotect(secondRow.Ciphertext));
        Assert.Throws<CryptographicException>(
            () => wrongProtector.Unprotect(secondRow.Ciphertext));

        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        SqliteConnection.ClearAllPools();
        foreach (string databaseFile in Directory.EnumerateFiles(
                     layout.Root,
                     $"{Path.GetFileName(layout.DatabasePath)}*"))
        {
            byte[] databaseBytes = await File.ReadAllBytesAsync(databaseFile);
            Assert.False(Contains(databaseBytes, Encoding.UTF8.GetBytes(original)));
            Assert.False(Contains(databaseBytes, Encoding.UTF8.GetBytes(replacement)));
        }

        Assert.DoesNotContain(
            logs,
            message => message.Contains(original, StringComparison.Ordinal)
                || message.Contains(replacement, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteIsIdempotentWithoutExposingPlaintext()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ISecretStore store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        SecretReference reference = await store.CreateAsync("integration-test", "delete-canary");

        Assert.True(await store.DeleteAsync(reference));
        Assert.False(await store.DeleteAsync(reference));
        Assert.DoesNotContain(
            typeof(ISecretStore).GetMethods(),
            method => method.Name.Contains("Get", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Read", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Retrieve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ISecretStore).GetMethods(),
            method => method.ReturnType == typeof(string)
                || method.ReturnType == typeof(Task<string>));
    }

    [Fact]
    public async Task InvalidPurposeAndMissingReplacementDoNotExposePlaintext()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ISecretStore store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        const string plaintext = "exception-canary-21a3c590";
        const string unsafePurpose = "purpose/secret-marker-44bb";

        ArgumentException purposeException = await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateAsync(unsafePurpose, plaintext));
        Assert.DoesNotContain(unsafePurpose, purposeException.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, purposeException.ToString(), StringComparison.Ordinal);

        KeyNotFoundException replacementException =
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => store.ReplaceAsync(new SecretReference(Guid.NewGuid()), plaintext));
        Assert.DoesNotContain(
            plaintext,
            replacementException.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptedCiphertextFailsPurposeBoundAndCanBeReplacedWithoutDisclosure()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ISecretStore store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        const string original = "corruption-original-canary-8cb359";
        const string replacement = "corruption-replacement-canary-f514ad";
        SecretReference reference = await store.CreateAsync("corruption-test", original);
        SecretRow originalRow = await ReadSecretRowAsync(provider, reference);
        IDataProtector protector = provider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(
                "TemperedTyrant.CreatorToolkit.ProtectedSecretStore.v1",
                originalRow.Purpose);

        const string corrupted = "not-valid-data-protection-ciphertext";
        CryptographicException failure = Assert.Throws<CryptographicException>(
            () => protector.Unprotect(corrupted));
        Assert.DoesNotContain(original, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(replacement, failure.ToString(), StringComparison.Ordinal);

        await using (AsyncServiceScope corruptionScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = corruptionScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            ProtectedSecretRecord record = await db.ProtectedSecrets.SingleAsync(
                candidate => candidate.Id == reference.Id);
            record.Ciphertext = corrupted;
            await db.SaveChangesAsync();
        }

        await store.ReplaceAsync(reference, replacement);
        SecretRow replaced = await ReadSecretRowAsync(provider, reference);
        Assert.Equal(replacement, protector.Unprotect(replaced.Ciphertext));
        Assert.DoesNotContain(original, replaced.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement, replaced.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(
            logs,
            message => message.Contains(original, StringComparison.Ordinal)
                || message.Contains(replacement, StringComparison.Ordinal));
    }

    private static async Task<SecretRow> ReadSecretRowAsync(
        ServiceProvider provider,
        SecretReference reference)
    {
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        await using SqliteConnection connection = new($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT Purpose, Ciphertext, Revision FROM ProtectedSecrets WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", reference.Id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SecretRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        return source.IndexOf(value) >= 0;
    }

    private sealed record SecretRow(string Purpose, string Ciphertext, long Revision);
}
