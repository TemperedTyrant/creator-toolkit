using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class DiscordPersistenceTests
{
    private const string SyntheticToken = "discord-bot-leak-canary-9f9d07d6416e4f139151";
    private const string ReplacementToken = "discord-bot-replacement-canary-7700246539c44e30";
    private const string ApplicationId = "200000000000000001";
    private const string BotId = "200000000000000002";
    private const string GuildId = "200000000000000003";
    private const string ChannelId = "200000000000000004";

    [Fact]
    public async Task ConnectionDestinationAndEncryptedTokenPersistAcrossRestartWithoutPlaintextColumn()
    {
        using TestDataDirectory data = new();
        var api = new FakeDiscordApi();
        Guid connectionId;

        await using (ServiceProvider provider = CreateProvider(data.Path, api))
        {
            await TestServices.InitializeAsync(provider);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IDiscordConfigurationService service = scope.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            DiscordOperationResult created = await service.CreateAsync(
                "Dedicated Creator Toolkit bot",
                SyntheticToken,
                Guid.NewGuid());
            Assert.Equal(DiscordOperationStatus.Succeeded, created.Status);
            connectionId = Assert.IsType<Guid>(created.Id);

            DiscordOperationResult saved = await service.SaveDestinationsAsync(
                connectionId,
                GuildId,
                [ChannelId],
                Guid.NewGuid());
            Assert.Equal(DiscordOperationStatus.Succeeded, saved.Status);
        }

        await using (ServiceProvider restarted = CreateProvider(data.Path, api))
        {
            await TestServices.InitializeAsync(restarted);
            await using AsyncServiceScope scope = restarted.CreateAsyncScope();
            IDiscordConfigurationService service = scope.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            DiscordConnectionDetails? details = await service.GetAsync(connectionId);
            Assert.NotNull(details);
            Assert.Single(details.Destinations);
            Assert.Equal(ChannelId, details.Destinations[0].ChannelId);

            Assert.Equal(
                DiscordOperationStatus.Succeeded,
                (await service.ReplaceTokenAsync(
                    connectionId,
                    details.Connection.Revision,
                    ReplacementToken,
                    Guid.NewGuid())).Status);

            IReadOnlyList<DiscordGuild> guilds = await service.ListGuildsAsync(connectionId);
            Assert.Single(guilds);
            Assert.True(api.ReceivedExpectedToken);
            Assert.True(api.ReceivedReplacementToken);
            Assert.True(api.LastGuildListUsedReplacementToken);
        }

        DataDirectoryLayout layout = DataDirectoryLayout.Prepare(data.Path);
        await using var connection = new SqliteConnection($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = "SELECT name FROM pragma_table_info('DiscordConnections') ORDER BY name;";
        List<string> columns = [];
        await using (SqliteDataReader reader = await schema.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain(columns, value => value.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ProtectedSecretId", columns);

        await using SqliteCommand secret = connection.CreateCommand();
        secret.CommandText = "SELECT Ciphertext FROM ProtectedSecrets LIMIT 1;";
        string ciphertext = Assert.IsType<string>(await secret.ExecuteScalarAsync());
        Assert.False(
            ciphertext.Contains(SyntheticToken, StringComparison.Ordinal),
            "The synthetic bot-token canary appeared in protected storage.");
        Assert.False(
            ciphertext.Contains(ReplacementToken, StringComparison.Ordinal),
            "The replacement bot-token canary appeared in protected storage.");
    }

    [Fact]
    public async Task UniqueDestinationAndCascadeDeletionAreEnforced()
    {
        using TestDataDirectory data = new();
        var api = new FakeDiscordApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IDiscordConfigurationService service = scope.ServiceProvider
            .GetRequiredService<IDiscordConfigurationService>();
        DiscordOperationResult created = await service.CreateAsync(
            "Discord",
            SyntheticToken,
            Guid.NewGuid());
        Guid id = Assert.IsType<Guid>(created.Id);

        Assert.Equal(
            DiscordOperationStatus.Succeeded,
            (await service.SaveDestinationsAsync(id, GuildId, [ChannelId, ChannelId], Guid.NewGuid())).Status);
        DiscordConnectionDetails details = Assert.IsType<DiscordConnectionDetails>(await service.GetAsync(id));
        Assert.Single(details.Destinations);

        DiscordDestinationListItem destination = details.Destinations[0];
        Assert.Equal(
            DiscordOperationStatus.Succeeded,
            (await service.SetDestinationEnabledAsync(
                destination.Id,
                destination.Revision,
                false,
                Guid.NewGuid())).Status);
        Assert.Equal(
            DiscordOperationStatus.StaleRevision,
            (await service.SetDestinationEnabledAsync(
                destination.Id,
                destination.Revision,
                true,
                Guid.NewGuid())).Status);
        Assert.Equal(
            DiscordOperationStatus.Succeeded,
            (await service.SetConnectionEnabledAsync(
                id,
                details.Connection.Revision,
                false,
                Guid.NewGuid())).Status);
        Assert.Equal(
            DiscordOperationStatus.StaleRevision,
            (await service.SetConnectionEnabledAsync(
                id,
                details.Connection.Revision,
                true,
                Guid.NewGuid())).Status);
        details = Assert.IsType<DiscordConnectionDetails>(await service.GetAsync(id));

        Assert.Equal(
            DiscordOperationStatus.Succeeded,
            (await service.DeleteConnectionAsync(id, details.Connection.Revision, Guid.NewGuid())).Status);
        CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.DiscordConnections.ToListAsync());
        Assert.Empty(await db.DiscordDestinations.ToListAsync());
        Assert.Empty(await db.ProtectedSecrets.ToListAsync());
    }

    private static ServiceProvider CreateProvider(string dataPath, IDiscordApi api) =>
        TestServices.Create(
            dataPath,
            configureServices: services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton(api);
            });

    private sealed class FakeDiscordApi : IDiscordApi
    {
        internal bool ReceivedExpectedToken { get; private set; }

        internal bool ReceivedReplacementToken { get; private set; }

        internal bool LastGuildListUsedReplacementToken { get; private set; }

        public Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken)
        {
            ReceivedExpectedToken |= token == SyntheticToken;
            ReceivedReplacementToken |= token == ReplacementToken;
            return Task.FromResult(new DiscordBotIdentity(BotId, "Creator Toolkit bot", ApplicationId));
        }

        public Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken)
        {
            ReceivedExpectedToken |= token == SyntheticToken;
            ReceivedReplacementToken |= token == ReplacementToken;
            LastGuildListUsedReplacementToken = token == ReplacementToken;
            return Task.FromResult<IReadOnlyList<DiscordGuild>>([new(GuildId, "Creators")]);
        }

        public Task<DiscordGuildDiscovery?> DiscoverGuildAsync(string token, DiscordBotIdentity identity, string guildId, CancellationToken cancellationToken)
        {
            ReceivedExpectedToken |= token == SyntheticToken;
            ReceivedReplacementToken |= token == ReplacementToken;
            return Task.FromResult<DiscordGuildDiscovery?>(new(
                new DiscordGuild(GuildId, "Creators"),
                [new DiscordChannelCapability(ChannelId, "announcements", 0, true, true, true, true, false)],
                [new DiscordRole(GuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false)],
                new DiscordGuildMember(BotId, "Creator Toolkit bot", [])));
        }

        public Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(string token, string guildId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuildMember>>([]);

        public Task<DiscordGuildMember?> GetMemberAsync(string token, string guildId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(null);

        public Task<DiscordApiSendResult> SendMessageAsync(string token, string channelId, DiscordMessageRequest request, DiscordValidatedImage? image, CancellationToken cancellationToken) =>
            Task.FromResult(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "200000000000000099"));
    }
}
