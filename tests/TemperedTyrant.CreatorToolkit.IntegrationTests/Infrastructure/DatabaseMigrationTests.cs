using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class DatabaseMigrationTests
{
    private static readonly string[] ExpectedApplicationTables =
    [
        "AuditRecords",
        "DiagnosticRecords",
        "InstallationStates",
        "Ownerships",
        "ProtectedSecrets",
        "SecurityCapabilities",
        "Workspaces",
    ];

    private static readonly string[] DeferredTables =
    [
        "Actions",
        "Announcements",
        "Deliveries",
        "Destinations",
        "EventSources",
        "PersistentJobs",
        "Schedules",
        "Workflows",
    ];

    [Fact]
    public async Task InitialMigrationCreatesIdentityAndSecurityFoundationOnly()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);

        await TestServices.InitializeAsync(provider);

        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        await using SqliteConnection connection = new($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();

        string[] tables = await ReadTablesAsync(connection);
        Assert.All(ExpectedApplicationTables, table => Assert.Contains(table, tables));
        Assert.Contains("AspNetUsers", tables);
        Assert.Contains("AspNetRoles", tables);
        Assert.Contains("__EFMigrationsHistory", tables);
        Assert.All(DeferredTables, table => Assert.DoesNotContain(table, tables));

        await using SqliteCommand roleCommand = connection.CreateCommand();
        roleCommand.CommandText = "SELECT Name, Id FROM AspNetRoles ORDER BY Name;";
        Dictionary<string, Guid> roles = [];
        await using SqliteDataReader reader = await roleCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            roles.Add(reader.GetString(0), reader.GetGuid(1));
        }

        Assert.Equal(
            new Dictionary<string, Guid>
            {
                ["Admin"] = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a02"),
                ["Editor"] = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a03"),
                ["Owner"] = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a01"),
                ["Viewer"] = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a04"),
            },
            roles);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext context = await contextFactory.CreateDbContextAsync();
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task InitialMigrationSeedsUninitializedSingletonState()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext context = await contextFactory.CreateDbContextAsync();

        var state = await context.InstallationStates.SingleAsync();
        Assert.Equal(1, state.Id);
        Assert.Null(state.InitializedAtUtc);
        Assert.Equal(0, state.Revision);
    }

    [Fact]
    public async Task StartupFailsWhenDatabasePathIsUnavailable()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        Directory.CreateDirectory(layout.DatabasePath);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestServices.InitializeAsync(provider));
        Assert.DoesNotContain(data.Path, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            logs,
            message => message.Contains(data.Path, StringComparison.Ordinal));
    }

    private static async Task<string[]> ReadTablesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        List<string> tables = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables];
    }
}
