using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class ProcessCoordinationTests
{
    [Fact]
    public async Task SecondWebHostIsRejectedButAdministrativeWorkCanRun()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider firstProvider = TestServices.Create(data.Path);
        await using ServiceProvider secondProvider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(firstProvider);

        await using ApplicationHostLease hostLease = await firstProvider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondProvider.GetRequiredService<ApplicationHostLock>().AcquireAsync());

        await secondProvider
            .GetRequiredService<MigrationCoordinator>()
            .EnsureCurrentForAdministrativeCommandAsync();

        bool securityOperationRan = false;
        await secondProvider
            .GetRequiredService<SecurityOperationCoordinator>()
            .ExecuteAsync(_ =>
            {
                securityOperationRan = true;
                return Task.CompletedTask;
            });
        Assert.True(securityOperationRan);

        await using AsyncServiceScope scope = secondProvider.CreateAsyncScope();
        ISecretStore secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        SecretReference secret = await secretStore.CreateAsync(
            "administrative-operation-test",
            "protected-value");
        Assert.True(await secretStore.DeleteAsync(secret));
    }

    [Fact]
    public async Task MigrationExecutionCannotRace()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider firstProvider = TestServices.Create(data.Path);
        await using ServiceProvider secondProvider = TestServices.Create(data.Path);

        await Task.WhenAll(
            firstProvider
                .GetRequiredService<MigrationCoordinator>()
                .MigrateForWebHostAsync(),
            secondProvider
                .GetRequiredService<MigrationCoordinator>()
                .MigrateForWebHostAsync());

        DataDirectoryLayout layout = firstProvider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        await using SqliteConnection connection = new($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM __EFMigrationsHistory
            WHERE MigrationId LIKE '%_InitialIdentityAndSecurityFoundation';
            """;

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task StaleLockFilesDoNotBlockStartupOrAdministrativeOperations()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        await TestServices.InitializeAsync(provider);
        await File.WriteAllTextAsync(
            Path.Combine(layout.LockPath, "application-host.lock"),
            "stale");
        await File.WriteAllTextAsync(Path.Combine(layout.LockPath, "migration.lock"), "stale");
        await File.WriteAllTextAsync(
            Path.Combine(layout.LockPath, "security-operation.lock"),
            "stale");

        await using ApplicationHostLease hostLease = await provider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();
        await provider
            .GetRequiredService<MigrationCoordinator>()
            .EnsureCurrentForAdministrativeCommandAsync();

        bool operationRan = false;
        await provider
            .GetRequiredService<SecurityOperationCoordinator>()
            .ExecuteAsync(_ =>
            {
                operationRan = true;
                return Task.CompletedTask;
            });
        Assert.True(operationRan);
    }

    [Fact]
    public async Task LockFailureDoesNotExposeItsConfiguredPath()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        Directory.CreateDirectory(Path.Combine(layout.LockPath, "application-host.lock"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<ApplicationHostLock>().AcquireAsync());
        Assert.DoesNotContain(data.Path, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationAndSecurityLocksSerializeTheirOperations()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider firstProvider = TestServices.Create(data.Path);
        await using ServiceProvider secondProvider = TestServices.Create(data.Path);

        await AssertSerializedAsync(
            operation => firstProvider
                .GetRequiredService<MigrationLock>()
                .ExecuteAsync(operation),
            operation => secondProvider
                .GetRequiredService<MigrationLock>()
                .ExecuteAsync(operation));

        await AssertSerializedAsync(
            operation => firstProvider
                .GetRequiredService<SecurityOperationCoordinator>()
                .ExecuteAsync(operation),
            operation => secondProvider
                .GetRequiredService<SecurityOperationCoordinator>()
                .ExecuteAsync(operation));
    }

    private static async Task AssertSerializedAsync(
        Func<Func<CancellationToken, Task>, Task> firstExecution,
        Func<Func<CancellationToken, Task>, Task> secondExecution)
    {
        int activeOperations = 0;
        int maximumConcurrency = 0;

        async Task Operation(CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeOperations);
            InterlockedExtensions.Max(ref maximumConcurrency, active);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            Interlocked.Decrement(ref activeOperations);
        }

        await Task.WhenAll(firstExecution(Operation), secondExecution(Operation));
        Assert.Equal(1, maximumConcurrency);
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref location, candidate, current) != current);
        }
    }
}
