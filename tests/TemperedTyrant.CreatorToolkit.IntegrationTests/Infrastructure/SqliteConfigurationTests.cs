using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class SqliteConfigurationTests
{
    [Fact]
    public async Task ConnectionsEnforceForeignKeysAndUseWal()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext context = await contextFactory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        Assert.Equal(1L, await ExecuteScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(30000L, await ExecuteScalarInt64Async(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(
            "wal",
            await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task InvalidOwnershipForeignKeysAreRejected()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;

        await using SqliteConnection connection = new(
            $"Data Source={layout.DatabasePath};Foreign Keys=True");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Ownerships (WorkspaceId, OwnerUserId, TransferredAtUtc, Revision)
            VALUES (1, $owner, $transferred, 0);
            """;
        command.Parameters.AddWithValue("$owner", Guid.NewGuid());
        command.Parameters.AddWithValue("$transferred", DateTimeOffset.UtcNow);

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task ConfiguredDataPathCannotInjectConnectionStringSettings()
    {
        using TestDataDirectory data = new();
        string configuredPath = Path.Combine(data.Path, "data;Foreign Keys=False");
        await using ServiceProvider provider = TestServices.Create(configuredPath);
        await TestServices.InitializeAsync(provider);
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        Assert.Equal(
            Path.Combine(configuredPath, "creator-toolkit.db"),
            layout.DatabasePath);
        Assert.True(File.Exists(layout.DatabasePath));

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext context = await contextFactory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        Assert.Equal(1L, await ExecuteScalarInt64Async(connection, "PRAGMA foreign_keys;"));
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PRAGMA returned no value."));
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PRAGMA returned no value."));
    }
}
