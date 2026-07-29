using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class DataProtectionStartupTests
{
    [Fact]
    public async Task KeyRingPersistsAcrossServiceProviderRestarts()
    {
        using TestDataDirectory data = new();
        const string plaintext = "restart-canary";
        string protectedValue;

        await using (ServiceProvider firstProvider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(firstProvider);
            IDataProtector protector = firstProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("integration-test");
            protectedValue = protector.Protect(plaintext);
        }

        await using (ServiceProvider secondProvider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(secondProvider);
            IDataProtector protector = secondProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("integration-test");
            Assert.Equal(plaintext, protector.Unprotect(protectedValue));
        }
    }

    [Fact]
    public async Task KeyRingUsesRestrictivePermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(layout.KeyRingPath));
        foreach (string keyFile in Directory.EnumerateFiles(layout.KeyRingPath))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(keyFile));
        }
    }

    [Fact]
    public void StartupFailsWhenKeyRingPathCannotBeCreated()
    {
        using TestDataDirectory data = new();
        File.WriteAllText(Path.Combine(data.Path, "keys"), "not-a-directory");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TestServices.Create(data.Path));
        Assert.DoesNotContain(data.Path, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-directory", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupFailsClosedWhenKeyStorageBecomesInaccessible()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        Directory.Delete(layout.KeyRingPath);
        await File.WriteAllTextAsync(layout.KeyRingPath, "key-storage-canary");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestServices.InitializeAsync(provider));
        Assert.DoesNotContain(data.Path, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "key-storage-canary",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            logs,
            message => message.Contains(data.Path, StringComparison.Ordinal)
                || message.Contains("key-storage-canary", StringComparison.Ordinal));
    }
}
