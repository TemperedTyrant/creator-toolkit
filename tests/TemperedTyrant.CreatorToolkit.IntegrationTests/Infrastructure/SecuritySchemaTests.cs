using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class SecuritySchemaTests
{
    [Fact]
    public async Task DatabaseEnforcesAtMostOneOwnershipButNotApplicationSoleOwnerInvariant()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using SqliteConnection connection = await OpenConnectionAsync(provider);
        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();

        await InsertUserAsync(connection, firstUserId, "First");
        await InsertUserAsync(connection, secondUserId, "Second");
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Workspaces (Id, TimeZoneId, CreatedAtUtc, Revision)
            VALUES (1, 'Etc/UTC', $now, 0);
            """,
            ("$now", DateTimeOffset.UtcNow));

        await InsertOwnershipAsync(connection, firstUserId);
        Assert.Equal(
            0L,
            await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = $user;",
                ("$user", firstUserId)));

        SqliteException duplicateOwnership = await Assert.ThrowsAsync<SqliteException>(
            () => InsertOwnershipAsync(connection, secondUserId));
        Assert.Equal(19, duplicateOwnership.SqliteErrorCode);

        SqliteException secondWorkspace = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO Workspaces (Id, TimeZoneId, CreatedAtUtc, Revision)
                VALUES (2, 'Etc/UTC', $now, 0);
                """,
                ("$now", DateTimeOffset.UtcNow)));
        Assert.Equal(19, secondWorkspace.SqliteErrorCode);

        Assert.Equal(1, await ExecuteAsync(connection, "DELETE FROM Ownerships;"));
        Assert.Equal(
            0L,
            await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM Ownerships;"));
    }

    [Fact]
    public async Task UserDeletionPreservesSecurityHistoryAndCannotDeleteTheOwner()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using SqliteConnection connection = await OpenConnectionAsync(provider);
        Guid ownerId = Guid.NewGuid();
        Guid ordinaryUserId = Guid.NewGuid();
        Guid capabilityId = Guid.NewGuid();
        Guid auditId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await InsertUserAsync(connection, ownerId, "Owner");
        await InsertUserAsync(connection, ordinaryUserId, "Ordinary");
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Workspaces (Id, TimeZoneId, CreatedAtUtc, Revision)
            VALUES (1, 'Etc/UTC', $now, 0);
            """,
            ("$now", now));
        await InsertOwnershipAsync(connection, ownerId);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO SecurityCapabilities
                (Id, Purpose, TokenHash, SubjectUserId, ActiveSlot, CreatedAtUtc,
                 ExpiresAtUtc, UsedAtUtc, RevokedAtUtc, CreatedByUserId, Revision)
            VALUES
                ($id, 'ActivateUser', randomblob(32), $user, NULL, $created,
                 $expires, NULL, NULL, $user, 0);
            """,
            ("$id", capabilityId),
            ("$user", ordinaryUserId),
            ("$created", now),
            ("$expires", now.AddHours(1)));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO AuditRecords
                (Id, OccurredAtUtc, EventCode, ActorUserId, TargetUserId, Outcome)
            VALUES
                ($id, $now, 'user.lifecycle', $user, $user, 'succeeded');
            """,
            ("$id", auditId),
            ("$now", now),
            ("$user", ordinaryUserId));

        SqliteException ownerDeletion = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                "DELETE FROM AspNetUsers WHERE Id = $id;",
                ("$id", ownerId)));
        Assert.Equal(19, ownerDeletion.SqliteErrorCode);

        Assert.Equal(
            1,
            await ExecuteAsync(
                connection,
                "DELETE FROM AspNetUsers WHERE Id = $id;",
                ("$id", ordinaryUserId)));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT COUNT(*) FROM SecurityCapabilities
                WHERE Id = $id AND SubjectUserId IS NULL AND CreatedByUserId IS NULL;
                """,
                ("$id", capabilityId)));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT COUNT(*) FROM AuditRecords
                WHERE Id = $id AND ActorUserId = $user AND TargetUserId = $user;
                """,
                ("$id", auditId),
                ("$user", ordinaryUserId)));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM Ownerships WHERE OwnerUserId = $owner;",
                ("$owner", ownerId)));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM InstallationStates WHERE Id = 1;"));
    }

    [Fact]
    public async Task CapabilitySlotUsesDeterministicStateAndExplicitRevocationAllowsReplacement()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using SqliteConnection connection = await OpenConnectionAsync(provider);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid expiredId = Guid.NewGuid();

        string indexSql = await ExecuteScalarStringAsync(
            connection,
            """
            SELECT sql FROM sqlite_master
            WHERE type = 'index'
              AND name = 'IX_SecurityCapabilities_Purpose_ActiveSlot';
            """);
        Assert.Contains("\"ActiveSlot\" IS NOT NULL", indexSql, StringComparison.Ordinal);
        Assert.DoesNotContain("CURRENT_", indexSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("datetime(", indexSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("julianday(", indexSql, StringComparison.OrdinalIgnoreCase);

        await InsertCapabilityAsync(
            connection,
            expiredId,
            "bootstrap-owner",
            now.AddHours(-2),
            now.AddHours(-1));
        SqliteException occupiedSlot = await Assert.ThrowsAsync<SqliteException>(
            () => InsertCapabilityAsync(
                connection,
                Guid.NewGuid(),
                "bootstrap-owner",
                now,
                now.AddHours(1)));
        Assert.Equal(19, occupiedSlot.SqliteErrorCode);

        SqliteException unclearedSlot = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                UPDATE SecurityCapabilities
                SET RevokedAtUtc = $revoked, Revision = Revision + 1
                WHERE Id = $id;
                """,
                ("$revoked", now),
                ("$id", expiredId)));
        Assert.Equal(19, unclearedSlot.SqliteErrorCode);

        Assert.Equal(
            1,
            await ExecuteAsync(
                connection,
                """
                UPDATE SecurityCapabilities
                SET RevokedAtUtc = $revoked, ActiveSlot = NULL, Revision = Revision + 1
                WHERE Id = $id;
                """,
                ("$revoked", now),
                ("$id", expiredId)));
        await InsertCapabilityAsync(
            connection,
            Guid.NewGuid(),
            "bootstrap-owner",
            now,
            now.AddHours(1));

        SqliteException shortHash = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO SecurityCapabilities
                    (Id, Purpose, TokenHash, ActiveSlot, CreatedAtUtc, ExpiresAtUtc, Revision)
                VALUES
                    ($id, 'RecoverOwner', randomblob(31), NULL, $created, $expires, 0);
                """,
                ("$id", Guid.NewGuid()),
                ("$created", now),
                ("$expires", now.AddHours(1))));
        Assert.Equal(19, shortHash.SqliteErrorCode);

        SqliteException malformedUseTime = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO SecurityCapabilities
                    (Id, Purpose, TokenHash, CreatedAtUtc, ExpiresAtUtc, UsedAtUtc, Revision)
                VALUES
                    ($id, 'ActivateUser', randomblob(32), $created, $expires, 'not-a-time', 0);
                """,
                ("$id", Guid.NewGuid()),
                ("$created", now),
                ("$expires", now.AddHours(1))));
        Assert.Equal(19, malformedUseTime.SqliteErrorCode);

        SqliteException conflictingTerminalState = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO SecurityCapabilities
                    (Id, Purpose, TokenHash, CreatedAtUtc, ExpiresAtUtc,
                     UsedAtUtc, RevokedAtUtc, Revision)
                VALUES
                    ($id, 'ActivateUser', randomblob(32), $created, $expires,
                     $terminal, $terminal, 0);
                """,
                ("$id", Guid.NewGuid()),
                ("$created", now),
                ("$expires", now.AddHours(1)),
                ("$terminal", now.AddMinutes(1))));
        Assert.Equal(19, conflictingTerminalState.SqliteErrorCode);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(ServiceProvider provider)
    {
        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        SqliteConnection connection = new(
            $"Data Source={layout.DatabasePath};Foreign Keys=True");
        await connection.OpenAsync();
        return connection;
    }

    private static Task<int> InsertUserAsync(
        SqliteConnection connection,
        Guid id,
        string displayName)
    {
        return ExecuteAsync(
            connection,
            """
            INSERT INTO AspNetUsers
                (Id, DisplayName, IsEnabled, CreatedAtUtc, EmailConfirmed,
                 PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES
                ($id, $displayName, 1, $now, 0, 0, 0, 1, 0);
            """,
            ("$id", id),
            ("$displayName", displayName),
            ("$now", DateTimeOffset.UtcNow));
    }

    private static Task<int> InsertOwnershipAsync(SqliteConnection connection, Guid userId)
    {
        return ExecuteAsync(
            connection,
            """
            INSERT INTO Ownerships (WorkspaceId, OwnerUserId, TransferredAtUtc, Revision)
            VALUES (1, $owner, $now, 0);
            """,
            ("$owner", userId),
            ("$now", DateTimeOffset.UtcNow));
    }

    private static Task<int> InsertCapabilityAsync(
        SqliteConnection connection,
        Guid id,
        string activeSlot,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        return ExecuteAsync(
            connection,
            """
            INSERT INTO SecurityCapabilities
                (Id, Purpose, TokenHash, ActiveSlot, CreatedAtUtc, ExpiresAtUtc, Revision)
            VALUES
                ($id, 'BootstrapOwner', randomblob(32), $slot, $created, $expires, 0);
            """,
            ("$id", id),
            ("$slot", activeSlot),
            ("$created", createdAt),
            ("$expires", expiresAt));
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameters(command, parameters);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The query returned no value."));
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The query returned no value."));
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }
}
