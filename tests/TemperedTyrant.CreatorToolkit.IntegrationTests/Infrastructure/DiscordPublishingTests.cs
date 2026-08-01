using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class DiscordPublishingTests
{
    private const string TokenCanary = "publishing-token-canary-c8b0c7f8d6384d62";
    private const string ContentCanary = "publishing-content-canary-72d76dc8dff14ce0";
    private const string GuildId = "300000000000000001";
    private const string BotId = "300000000000000002";
    private const string ApplicationId = "300000000000000003";
    private const string ChannelOne = "300000000000000004";
    private const string ChannelTwo = "300000000000000005";

    [Fact]
    public async Task OneChannelFailureDoesNotBlockLaterSuccessAndDuplicateUsesStableNonces()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        Guid connectionId;
        DiscordDestinationListItem[] destinations;
        await using (AsyncServiceScope setup = provider.CreateAsyncScope())
        {
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await setup.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
                    announcementId,
                    "Discord announcement",
                    ContentCanary,
                    actor)).Status);
            IDiscordConfigurationService configuration = setup.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>((await configuration.CreateAsync(
                "Discord",
                TokenCanary,
                actor)).Id);
            Assert.Equal(
                DiscordOperationStatus.Succeeded,
                (await configuration.SaveDestinationsAsync(
                    connectionId,
                    GuildId,
                    [ChannelOne, ChannelTwo],
                    actor)).Status);
            destinations = (await configuration.GetAsync(connectionId))!.Destinations.ToArray();
        }

        Guid submissionId = Guid.NewGuid();
        DiscordPublishRequest request = CreateRequest(
            submissionId,
            announcementId,
            connectionId,
            destinations);
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.MissingPermission));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099"));
        await using (AsyncServiceScope publishScope = provider.CreateAsyncScope())
        {
            DiscordPublicationResult result = await publishScope.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .PublishAsync(request, false, actor);
            Assert.Equal(
                [DiscordDeliveryStatus.MissingPermission, DiscordDeliveryStatus.Success],
                result.Channels.Select(value => value.Status));
        }

        Assert.Equal(2, api.Messages.Count);
        Assert.NotEqual(api.Messages[0].Nonce, api.Messages[1].Nonce);
        string[] firstNonces = api.Messages.Select(value => value.Nonce).ToArray();

        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000098"));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000097"));
        await using (AsyncServiceScope duplicateScope = provider.CreateAsyncScope())
        {
            _ = await duplicateScope.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .PublishAsync(request, false, actor);
        }

        Assert.Equal(firstNonces, api.Messages.Skip(2).Select(value => value.Nonce));
        Assert.All(api.Messages, message =>
        {
            Assert.True(message.EnforceNonce);
            Assert.Empty(message.AllowedMentions.Parse);
        });

        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        string auditText = string.Join(
            '|',
            await db.AuditRecords
                .Where(value => value.EventCode.StartsWith("discord.publication"))
                .Select(value => value.EventCode + ":" + value.Outcome)
                .ToArrayAsync());
        Assert.Equal(6, await db.AuditRecords.CountAsync(value => value.EventCode.StartsWith("discord.publication")));
        Assert.False(
            auditText.Contains(ContentCanary, StringComparison.Ordinal),
            "The composed-content canary appeared in audit metadata.");
        Assert.False(
            auditText.Contains(TokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in audit metadata.");
    }

    [Fact]
    public async Task AuthenticationFailureStopsFurtherDiscordCallsInTheOperation()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        await using AsyncServiceScope setup = provider.CreateAsyncScope();
        await setup.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
            announcementId,
            "Discord announcement",
            "Safe content",
            actor);
        IDiscordConfigurationService configuration = setup.ServiceProvider.GetRequiredService<IDiscordConfigurationService>();
        Guid connectionId = Assert.IsType<Guid>((await configuration.CreateAsync("Discord", TokenCanary, actor)).Id);
        await configuration.SaveDestinationsAsync(connectionId, GuildId, [ChannelOne, ChannelTwo], actor);
        DiscordDestinationListItem[] destinations = (await configuration.GetAsync(connectionId))!.Destinations.ToArray();
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.AuthenticationFailed));

        DiscordPublicationResult result = await setup.ServiceProvider
            .GetRequiredService<IDiscordPublishingService>()
            .PublishAsync(CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations), false, actor);

        Assert.Single(api.Messages);
        Assert.All(result.Channels, value => Assert.Equal(DiscordDeliveryStatus.AuthenticationFailed, value.Status));
    }

    [Fact]
    public async Task DiscoveryAuthenticationAndAvailabilityFailuresReturnPerChannelSafeResults()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        Guid connectionId;
        DiscordDestinationListItem[] destinations;
        await using (AsyncServiceScope setup = provider.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
                announcementId,
                "Discovery failure",
                "Safe content",
                actor);
            IDiscordConfigurationService configuration = setup.ServiceProvider.GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>((await configuration.CreateAsync("Discord", TokenCanary, actor)).Id);
            await configuration.SaveDestinationsAsync(connectionId, GuildId, [ChannelOne], actor);
            destinations = (await configuration.GetAsync(connectionId))!.Destinations.ToArray();
        }

        api.DiscoveryFailure = new DiscordApiUnavailableException();
        await using (AsyncServiceScope unavailableScope = provider.CreateAsyncScope())
        {
            DiscordPublicationResult unavailable = await unavailableScope.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .PublishAsync(
                    CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations),
                    false,
                    actor);
            Assert.All(unavailable.Channels, value => Assert.Equal(DiscordDeliveryStatus.DiscordUnavailable, value.Status));
        }

        api.DiscoveryFailure = new DiscordApiAuthenticationException();
        await using AsyncServiceScope authenticationScope = provider.CreateAsyncScope();
        DiscordPublicationResult authentication = await authenticationScope.ServiceProvider
            .GetRequiredService<IDiscordPublishingService>()
            .PublishAsync(
                CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations),
                false,
                actor);
        Assert.All(authentication.Channels, value => Assert.Equal(DiscordDeliveryStatus.AuthenticationFailed, value.Status));
        Assert.Empty(api.Messages);
    }

    [Fact]
    public void RichEmbedUsesExplicitAttachmentMetadataAndDoesNotFetchRemoteUrls()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
        DiscordValidatedImage image = DiscordImageValidation.Validate(
            png,
            "safe.png",
            "image/png",
            "Alt text",
            true,
            true,
            Guid.Empty);
        DiscordPublishRequest request = new(
            Guid.Empty,
            Guid.Empty,
            1,
            Guid.Empty,
            GuildId,
            [],
            DiscordMessageMode.Embed,
            null,
            true,
            new DiscordEmbedInput("Above", "Title", "Description", "https://example.invalid/title", "#74c7a5", "Footer", null, "https://example.invalid/thumb.png"),
            DiscordMentionSelection.None,
            false,
            null,
            image);

        DiscordMessageRequest message = DiscordPublishingService.BuildMessage(
            request,
            DiscordMentionSelection.None.Build());

        DiscordAttachmentPayload attachment = Assert.Single(message.Attachments!);
        Assert.True(attachment.IsSpoiler);
        Assert.Equal("Alt text", attachment.Description);
        Assert.Equal("attachment://image-00000000000000000000000000000000.png", Assert.Single(message.Embeds!).Image!.Url);
    }

    [Fact]
    public async Task OverallTimeoutReturnsSafeTimedOutResultsAndRequestCancellationIsDistinct()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(
            data.Path,
            api,
            TimeSpan.FromMilliseconds(50));
        await TestServices.InitializeAsync(provider);
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        Guid connectionId;
        DiscordDestinationListItem[] destinations;
        await using (AsyncServiceScope setup = provider.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
                announcementId,
                "Timeout announcement",
                "Safe timeout content",
                actor);
            IDiscordConfigurationService configuration = setup.ServiceProvider.GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>((await configuration.CreateAsync("Discord", TokenCanary, actor)).Id);
            await configuration.SaveDestinationsAsync(connectionId, GuildId, [ChannelOne], actor);
            destinations = (await configuration.GetAsync(connectionId))!.Destinations.ToArray();
        }

        api.BlockDiscovery = true;
        await using (AsyncServiceScope timeoutScope = provider.CreateAsyncScope())
        {
            DiscordPublicationResult timedOut = await timeoutScope.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .PublishAsync(
                    CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations),
                    false,
                    actor);
            Assert.All(timedOut.Channels, value => Assert.Equal(DiscordDeliveryStatus.TimedOut, value.Status));
        }

        api.BlockDiscovery = false;
        api.BlockSend = true;
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await using AsyncServiceScope cancellationScope = provider.CreateAsyncScope();
        DiscordPublicationResult cancelledResult = await cancellationScope.ServiceProvider
            .GetRequiredService<IDiscordPublishingService>()
            .PublishAsync(
                CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations),
                false,
                actor,
                cancelled.Token);
        Assert.All(cancelledResult.Channels, value => Assert.Equal(DiscordDeliveryStatus.Cancelled, value.Status));
    }

    [Fact]
    public async Task AnnouncementChangedDuringLiveDiscoveryIsRejectedBeforeAnyChannelSend()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        Guid connectionId;
        DiscordDestinationListItem[] destinations;
        await using (AsyncServiceScope setup = provider.CreateAsyncScope())
        {
            IAnnouncementService announcements = setup.ServiceProvider.GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await announcements.CreateAsync(
                    announcementId,
                    "Revision race",
                    "Original safe content",
                    actor)).Status);
            IDiscordConfigurationService configuration = setup.ServiceProvider.GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>((await configuration.CreateAsync("Discord", TokenCanary, actor)).Id);
            await configuration.SaveDestinationsAsync(connectionId, GuildId, [ChannelOne], actor);
            destinations = (await configuration.GetAsync(connectionId))!.Destinations.ToArray();
        }

        api.DiscoveryCallback = async () =>
        {
            await using AsyncServiceScope mutation = provider.CreateAsyncScope();
            AnnouncementOperationResult updated = await mutation.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .UpdateAsync(
                    announcementId,
                    "Revision race updated",
                    "Updated safe content",
                    1,
                    actor);
            Assert.Equal(AnnouncementOperationStatus.Succeeded, updated.Status);
        };

        await using AsyncServiceScope publish = provider.CreateAsyncScope();
        DiscordPublicationValidationException failure = await Assert.ThrowsAsync<DiscordPublicationValidationException>(
            () => publish.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .PublishAsync(
                    CreateRequest(Guid.NewGuid(), announcementId, connectionId, destinations),
                    false,
                    actor));

        Assert.Equal(
            "The announcement changed or is no longer a Draft. Review it again before publishing.",
            failure.Message);
        Assert.Empty(api.Messages);
    }

    private static DiscordPublishRequest CreateRequest(
        Guid submissionId,
        Guid announcementId,
        Guid connectionId,
        IReadOnlyList<DiscordDestinationListItem> destinations) =>
        new(
            submissionId,
            announcementId,
            1,
            connectionId,
            GuildId,
            destinations.Select(value => value.Id).ToArray(),
            DiscordMessageMode.Plain,
            ContentCanary,
            false,
            null,
            DiscordMentionSelection.None,
            false,
            null,
            null);

    private static ServiceProvider CreateProvider(
        string dataPath,
        IDiscordApi api,
        TimeSpan? timeout = null) =>
        TestServices.Create(
            dataPath,
            configureServices: services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton(api);
                if (timeout is not null)
                {
                    services.RemoveAll<DiscordPublishingOptions>();
                    services.AddSingleton(new DiscordPublishingOptions(timeout.Value));
                }
            });

    private sealed class PublishingApi : IDiscordApi
    {
        internal Queue<DiscordApiSendResult> Results { get; } = new();

        internal List<DiscordMessageRequest> Messages { get; } = [];

        internal bool BlockDiscovery { get; set; }

        internal bool BlockSend { get; set; }

        internal Func<Task>? DiscoveryCallback { get; set; }

        internal Exception? DiscoveryFailure { get; set; }

        public Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new DiscordBotIdentity(BotId, "Creator Toolkit bot", ApplicationId));

        public Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuild>>([new DiscordGuild(GuildId, "Creators")]);

        public async Task<DiscordGuildDiscovery?> DiscoverGuildAsync(string token, DiscordBotIdentity identity, string guildId, CancellationToken cancellationToken)
        {
            if (DiscoveryFailure is not null)
            {
                Exception failure = DiscoveryFailure;
                DiscoveryFailure = null;
                if (failure is DiscordApiAuthenticationException)
                {
                    throw new DiscordApiAuthenticationException();
                }

                throw new DiscordApiUnavailableException();
            }

            if (BlockDiscovery)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (DiscoveryCallback is not null)
            {
                Func<Task> callback = DiscoveryCallback;
                DiscoveryCallback = null;
                await callback();
            }

            return new DiscordGuildDiscovery(
                new DiscordGuild(GuildId, "Creators"),
                [
                    new DiscordChannelCapability(ChannelOne, "first", 0, true, true, true, true, false),
                    new DiscordChannelCapability(ChannelTwo, "second", 0, true, true, true, true, false),
                ],
                [new DiscordRole(GuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false)],
                new DiscordGuildMember(BotId, "Creator Toolkit bot", []));
        }

        public Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(string token, string guildId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuildMember>>([]);

        public Task<DiscordGuildMember?> GetMemberAsync(string token, string guildId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(new DiscordGuildMember(userId, "Member", []));

        public async Task<DiscordApiSendResult> SendMessageAsync(string token, string channelId, DiscordMessageRequest request, DiscordValidatedImage? image, CancellationToken cancellationToken)
        {
            Messages.Add(request);
            if (BlockSend)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Results.Count == 0
                ? new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099")
                : Results.Dequeue();
        }
    }
}
