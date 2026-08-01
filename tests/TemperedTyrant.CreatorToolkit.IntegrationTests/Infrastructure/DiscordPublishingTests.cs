using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;
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
    private static readonly DateTimeOffset InitialTime = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DiscordHttpClientSuppressesDefaultUriLogging()
    {
        using TestDataDirectory data = new();
        using ServiceProvider provider = TestServices.Create(data.Path);

        HttpClientFactoryOptions options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(DiscordHttpApi));

        bool suppressDefaultLogging = Assert.IsType<bool>(
            typeof(HttpClientFactoryOptions)
                .GetProperty(
                    "SuppressDefaultLogging",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(options));
        Assert.True(suppressDefaultLogging);
    }

    [Fact]
    public async Task ConfirmationEnqueuesWithoutDiscordAndDuplicateReturnsSamePublication()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        Guid submissionId = Guid.NewGuid();
        DiscordPublishRequest request = CreateRequest(
            submissionId,
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations);
        api.ResetOperationCounts();

        DiscordPublicationEnqueueResult first;
        DiscordPublicationEnqueueResult duplicate;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IDiscordPublishingService service = scope.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>();
            first = await service.EnqueueAsync(request, false, setup.ActorId);
            duplicate = await service.EnqueueAsync(request, false, setup.ActorId);
        }

        Assert.False(first.Existing);
        Assert.True(duplicate.Existing);
        Assert.Equal(first.PublicationId, duplicate.PublicationId);
        Assert.Equal(0, api.DiscoveryCalls);
        Assert.Equal(0, api.SendCalls);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(1, await db.Publications.CountAsync());
        Assert.Equal(1, await db.PublicationDeliveries.CountAsync());
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(value => value.EventCode == "publication.queued"));
        PublicationPayload payload = await db.PublicationPayloads.SingleAsync();
        string ciphertextText = Encoding.UTF8.GetString(payload.Ciphertext);
        Assert.False(
            ciphertextText.Contains(ContentCanary, StringComparison.Ordinal),
            "The publication-content canary appeared in durable plaintext.");
        Assert.False(
            ciphertextText.Contains(TokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in durable plaintext.");
    }

    [Fact]
    public async Task ReviewFailsClosedWhenLiveChannelCanNoLongerSend()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        DiscordPublishRequest request = CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations);
        var discovery = new DiscordGuildDiscovery(
            new DiscordGuild(GuildId, "Synthetic server"),
            [new DiscordChannelCapability(ChannelOne, "Synthetic channel", 0, true, false, true, true, false)],
            [new DiscordRole(GuildId, "everyone", "0", false)],
            new DiscordGuildMember(BotId, "Synthetic bot", []));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IDiscordPublishingService service = scope.ServiceProvider
            .GetRequiredService<IDiscordPublishingService>();
        DiscordPublicationValidationException exception = await Assert.ThrowsAsync<DiscordPublicationValidationException>(
            () => service.ValidateReviewAsync(request, false, discovery));

        Assert.Equal(
            "The bot can no longer send to every selected Discord channel.",
            exception.Message);
        Assert.Equal(0, api.SendCalls);
        CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.Publications.ToArrayAsync());
    }

    [Fact]
    public async Task PerDestinationFailureIsIsolatedAndTerminalPayloadIsRemoved()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne, ChannelTwo]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.MissingPermission));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099"));

        Assert.True(await ProcessNextAsync(provider));
        Assert.True(await ProcessNextAsync(provider));

        Assert.Equal(2, api.SendCalls);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Publication publication = await db.Publications.Include(value => value.Deliveries).SingleAsync();
        Assert.Equal(PublicationStatus.PartiallySucceeded, publication.Status);
        Assert.Equal(1, publication.SuccessfulDeliveryCount);
        Assert.Equal(1, publication.FailedDeliveryCount);
        Assert.Empty(await db.PublicationPayloads.ToArrayAsync());
    }

    [Fact]
    public async Task TransientRetrySurvivesAndReusesTheStableNonce()
    {
        using TestDataDirectory data = new();
        var time = new ManualTimeProvider(InitialTime);
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api, time);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        byte[] imageBytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
        DiscordValidatedImage image = DiscordImageValidation.Validate(
            imageBytes.ToArray(),
            "retry.png",
            "image/png",
            null,
            false,
            false,
            Guid.NewGuid());
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations,
            image));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable));
        api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099"));

        Assert.True(await ProcessNextAsync(provider));
        await using (AsyncServiceScope retryState = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = retryState.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            PublicationDelivery delivery = await db.PublicationDeliveries.SingleAsync();
            Assert.Equal(PublicationDeliveryStatus.RetryScheduled, delivery.Status);
            Assert.Equal(InitialTime.AddSeconds(30), delivery.NextAttemptAtUtc);
            Assert.Equal(1, await db.PublicationPayloads.CountAsync());
        }

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await ProcessNextAsync(provider));
        Assert.Equal(2, api.Messages.Count);
        Assert.Equal(api.Messages[0].Nonce, api.Messages[1].Nonce);
        Assert.All(api.Messages, value => Assert.True(value.EnforceNonce));
        Assert.Equal(2, api.Images.Count);
        Assert.All(api.Images, value => Assert.Equal(imageBytes, value));
        Assert.NotSame(api.Images[0], api.Images[1]);
    }

    [Fact]
    public async Task TransientFailuresStopAfterFourAttempts()
    {
        using TestDataDirectory data = new();
        var time = new ManualTimeProvider(InitialTime);
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api, time);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));
        for (int count = 0; count < PublicationRetryPolicy.MaximumAttempts; count++)
        {
            api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable));
        }

        Assert.True(await ProcessNextAsync(provider));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await ProcessNextAsync(provider));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await ProcessNextAsync(provider));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await ProcessNextAsync(provider));

        Assert.Equal(PublicationRetryPolicy.MaximumAttempts, api.SendCalls);
        Assert.Single(api.Messages.Select(value => value.Nonce).Distinct(StringComparer.Ordinal));
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(PublicationStatus.Failed, (await db.Publications.SingleAsync()).Status);
        Assert.Equal(
            PublicationDeliveryStatus.FailedPermanent,
            (await db.PublicationDeliveries.SingleAsync()).Status);
        Assert.Equal(PublicationRetryPolicy.MaximumAttempts, await db.PublicationAttempts.CountAsync());
        Assert.Empty(await db.PublicationPayloads.ToArrayAsync());
    }

    [Fact]
    public async Task RateLimitUsesBoundedDiscordRetryAfterDurably()
    {
        using TestDataDirectory data = new();
        var time = new ManualTimeProvider(InitialTime);
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api, time);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));
        api.Results.Enqueue(new DiscordApiSendResult(
            DiscordDeliveryStatus.RateLimited,
            RetryAfter: TimeSpan.FromSeconds(90)));

        Assert.True(await ProcessNextAsync(provider));
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        PublicationDelivery delivery = await verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .PublicationDeliveries.SingleAsync();
        Assert.Equal(PublicationDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.Equal(InitialTime.AddSeconds(90), delivery.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(DiscordDeliveryStatus.AuthenticationFailed, "authentication-failed")]
    [InlineData(DiscordDeliveryStatus.MissingPermission, "missing-permission")]
    [InlineData(DiscordDeliveryStatus.DestinationUnavailable, "destination-unavailable")]
    public async Task PermanentDiscordOutcomesAreNotRetried(
        DiscordDeliveryStatus discordStatus,
        string expectedSafeOutcome)
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));
        api.Results.Enqueue(new DiscordApiSendResult(discordStatus));

        Assert.True(await ProcessNextAsync(provider));
        Assert.False(await ProcessNextAsync(provider));
        Assert.Equal(1, api.SendCalls);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        PublicationDelivery delivery = await verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .PublicationDeliveries.SingleAsync();
        Assert.Equal(PublicationDeliveryStatus.FailedPermanent, delivery.Status);
        Assert.Equal(expectedSafeOutcome, delivery.LastSafeOutcome);
    }

    [Fact]
    public async Task CorruptedProtectedPayloadFailsPermanentlyWithoutDiscordOrPlaintextHistory()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));
        await using (AsyncServiceScope corruption = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = corruption.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            PublicationPayload payload = await db.PublicationPayloads.SingleAsync();
            byte[] corrupted = payload.Ciphertext.ToArray();
            corrupted[0] ^= 0xff;
            payload.Ciphertext = corrupted;
            await db.SaveChangesAsync();
        }

        Assert.True(await ProcessNextAsync(provider));
        Assert.Equal(0, api.SendCalls);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext database = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        PublicationDelivery delivery = await database.PublicationDeliveries.SingleAsync();
        Assert.Equal(PublicationDeliveryStatus.FailedPermanent, delivery.Status);
        Assert.Equal("protected-payload-invalid", delivery.LastSafeOutcome);
        Assert.Empty(await database.PublicationPayloads.ToArrayAsync());
    }

    [Fact]
    public async Task ProtectedImageAndScheduledRetrySurviveServiceProviderRestart()
    {
        using TestDataDirectory data = new();
        var time = new ManualTimeProvider(InitialTime);
        var api = new PublishingApi();
        const string imageCanary = "durable-image-canary-789ad06f2b65";
        byte[] expectedImage =
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            .. Encoding.UTF8.GetBytes(imageCanary),
        ];
        PublicationSetup setup;
        await using (ServiceProvider first = CreateProvider(data.Path, api, time))
        {
            await TestServices.InitializeAsync(first);
            setup = await CreatePublicationSetupAsync(first, api, [ChannelOne]);
            DiscordValidatedImage image = DiscordImageValidation.Validate(
                expectedImage.ToArray(),
                "synthetic.png",
                "image/png",
                "Synthetic image",
                true,
                true,
                Guid.NewGuid());
            await EnqueueAsync(first, setup, CreateRequest(
                Guid.NewGuid(),
                setup.AnnouncementId,
                setup.ConnectionId,
                setup.Destinations,
                image));
            api.Results.Enqueue(new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable));
            DataDirectoryLayout layout = first
                .GetRequiredService<DataDirectoryLayoutProvider>()
                .Layout;
            string databaseText = Encoding.Latin1.GetString(
                await File.ReadAllBytesAsync(layout.DatabasePath));
            Assert.False(
                databaseText.Contains(imageCanary, StringComparison.Ordinal),
                "The uploaded-image canary appeared in SQLite plaintext.");
            Assert.True(await ProcessNextAsync(first));
        }

        time.Advance(TimeSpan.FromSeconds(30));
        await using ServiceProvider restarted = CreateProvider(data.Path, api, time);
        await TestServices.InitializeAsync(restarted);
        Assert.True(await ProcessNextAsync(restarted));
        Assert.Equal(2, api.Images.Count);
        Assert.All(api.Images, value => Assert.Equal(expectedImage, value));
        Assert.NotSame(api.Images[0], api.Images[1]);
        await using AsyncServiceScope verification = restarted.CreateAsyncScope();
        Assert.Empty(await verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .PublicationPayloads.ToArrayAsync());
    }

    [Fact]
    public async Task CancellationPreventsPendingSendAndRemovesPayload()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        Guid publicationId = await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));

        await using (AsyncServiceScope cancelScope = provider.CreateAsyncScope())
        {
            IPublicationHistoryService history = cancelScope.ServiceProvider
                .GetRequiredService<IPublicationHistoryService>();
            PublicationHistoryDetails details = Assert.IsType<PublicationHistoryDetails>(
                await history.GetAsync(publicationId));
            Assert.Equal(
                PublicationCancellationResult.Succeeded,
                await history.CancelAsync(
                    publicationId,
                    details.Publication.Revision,
                    setup.ActorId));
        }

        Assert.False(await ProcessNextAsync(provider));
        Assert.Equal(0, api.SendCalls);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(PublicationStatus.Cancelled, (await db.Publications.SingleAsync()).Status);
        Assert.Empty(await db.PublicationPayloads.ToArrayAsync());
    }

    [Fact]
    public async Task CancellationDoesNotRollBackMessageAcceptedWhileDeliveryIsLeased()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi
        {
            SendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ControlledSend = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        Guid publicationId = await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));

        Task<bool> processing = ProcessNextAsync(provider);
        await api.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (AsyncServiceScope cancelScope = provider.CreateAsyncScope())
        {
            IPublicationHistoryService history = cancelScope.ServiceProvider
                .GetRequiredService<IPublicationHistoryService>();
            PublicationHistoryDetails details = Assert.IsType<PublicationHistoryDetails>(
                await history.GetAsync(publicationId));
            Assert.Equal(
                PublicationCancellationResult.Succeeded,
                await history.CancelAsync(
                    publicationId,
                    details.Publication.Revision,
                    setup.ActorId));
        }

        api.ControlledSend.SetResult(
            new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099"));
        Assert.True(await processing);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(PublicationDeliveryStatus.Succeeded, (await db.PublicationDeliveries.SingleAsync()).Status);
        Assert.Equal(PublicationStatus.Succeeded, (await db.Publications.SingleAsync()).Status);
    }

    [Fact]
    public async Task InterruptedAttemptRecoversExpiredLeaseWithSameNonce()
    {
        using TestDataDirectory data = new();
        var time = new ManualTimeProvider(InitialTime);
        var api = new PublishingApi
        {
            SendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ControlledSend = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using ServiceProvider provider = CreateProvider(data.Path, api, time);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));

        using var interrupted = new CancellationTokenSource();
        Task<bool> abandoned = ProcessNextAsync(provider, interrupted.Token, "first-lease-owner");
        await api.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        interrupted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        time.Advance(TimeSpan.FromSeconds(45));
        api.ControlledSend.SetResult(
            new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099"));

        Assert.True(await ProcessNextAsync(provider, CancellationToken.None, "recovery-lease-owner"));
        Assert.Equal(2, api.Messages.Count);
        Assert.Equal(api.Messages[0].Nonce, api.Messages[1].Nonce);
        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        PublicationAttempt[] attempts = await db.PublicationAttempts
            .OrderBy(value => value.AttemptNumber)
            .ToArrayAsync();
        Assert.Equal(2, attempts.Length);
        Assert.Equal("abandoned", attempts[0].SafeOutcome);
        Assert.Equal("success", attempts[1].SafeOutcome);
    }

    [Fact]
    public async Task AnnouncementDeletionIsBlockedUntilRelatedPublicationIsTerminal()
    {
        using TestDataDirectory data = new();
        var api = new PublishingApi();
        await using ServiceProvider provider = CreateProvider(data.Path, api);
        await TestServices.InitializeAsync(provider);
        PublicationSetup setup = await CreatePublicationSetupAsync(provider, api, [ChannelOne]);
        Guid publicationId = await EnqueueAsync(provider, setup, CreateRequest(
            Guid.NewGuid(),
            setup.AnnouncementId,
            setup.ConnectionId,
            setup.Destinations));

        await using (AsyncServiceScope active = provider.CreateAsyncScope())
        {
            IAnnouncementService announcements = active.ServiceProvider
                .GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.InvalidTransition,
                (await announcements.DeleteAsync(
                    setup.AnnouncementId,
                    1,
                    setup.ActorId)).Status);
            IPublicationHistoryService history = active.ServiceProvider
                .GetRequiredService<IPublicationHistoryService>();
            PublicationHistoryDetails details = Assert.IsType<PublicationHistoryDetails>(
                await history.GetAsync(publicationId));
            Assert.Equal(
                PublicationCancellationResult.Succeeded,
                await history.CancelAsync(
                    publicationId,
                    details.Publication.Revision,
                    setup.ActorId));
        }

        await using (AsyncServiceScope terminal = provider.CreateAsyncScope())
        {
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await terminal.ServiceProvider.GetRequiredService<IAnnouncementService>()
                    .DeleteAsync(setup.AnnouncementId, 1, setup.ActorId)).Status);
            PublicationHistoryDetails history = Assert.IsType<PublicationHistoryDetails>(
                await terminal.ServiceProvider.GetRequiredService<IPublicationHistoryService>()
                    .GetAsync(publicationId));
            Assert.Null(history.Publication.AnnouncementId);
        }
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
        Assert.Equal(
            "attachment://image-00000000000000000000000000000000.png",
            Assert.Single(message.Embeds!).Image!.Url);
    }

    private static async Task<PublicationSetup> CreatePublicationSetupAsync(
        ServiceProvider provider,
        PublishingApi api,
        IReadOnlyList<string> channelIds)
    {
        Guid actor = Guid.NewGuid();
        Guid announcementId = Guid.NewGuid();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Assert.Equal(
            AnnouncementOperationStatus.Succeeded,
            (await scope.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
                announcementId,
                "Discord announcement",
                ContentCanary,
                actor)).Status);
        IDiscordConfigurationService configuration = scope.ServiceProvider
            .GetRequiredService<IDiscordConfigurationService>();
        Guid connectionId = Assert.IsType<Guid>((await configuration.CreateAsync(
            "Discord",
            TokenCanary,
            actor)).Id);
        Assert.Equal(
            DiscordOperationStatus.Succeeded,
            (await configuration.SaveDestinationsAsync(
                connectionId,
                GuildId,
                channelIds,
                actor)).Status);
        DiscordDestinationListItem[] destinations = (await configuration.GetAsync(connectionId))!
            .Destinations.ToArray();
        api.ResetOperationCounts();
        return new PublicationSetup(actor, announcementId, connectionId, destinations);
    }

    private static async Task<Guid> EnqueueAsync(
        ServiceProvider provider,
        PublicationSetup setup,
        DiscordPublishRequest request)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        DiscordPublicationEnqueueResult result = await scope.ServiceProvider
            .GetRequiredService<IDiscordPublishingService>()
            .EnqueueAsync(request, false, setup.ActorId);
        return result.PublicationId;
    }

    private static Task<bool> ProcessNextAsync(ServiceProvider provider) =>
        ProcessNextAsync(provider, CancellationToken.None, "test-lease-owner");

    private static async Task<bool> ProcessNextAsync(
        ServiceProvider provider,
        CancellationToken cancellationToken,
        string leaseOwner = "test-lease-owner")
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublicationProcessor>()
            .ProcessNextAsync(leaseOwner, cancellationToken);
    }

    private static DiscordPublishRequest CreateRequest(
        Guid submissionId,
        Guid announcementId,
        Guid connectionId,
        IReadOnlyList<DiscordDestinationListItem> destinations,
        DiscordValidatedImage? image = null) =>
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
            image);

    private static ServiceProvider CreateProvider(
        string dataPath,
        IDiscordApi api,
        TimeProvider? timeProvider = null) =>
        TestServices.Create(
            dataPath,
            timeProvider: timeProvider,
            configureServices: services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton(api);
            });

    private sealed record PublicationSetup(
        Guid ActorId,
        Guid AnnouncementId,
        Guid ConnectionId,
        IReadOnlyList<DiscordDestinationListItem> Destinations);

    private sealed class PublishingApi : IDiscordApi
    {
        internal Queue<DiscordApiSendResult> Results { get; } = new();

        internal List<DiscordMessageRequest> Messages { get; } = [];

        internal List<byte[]> Images { get; } = [];

        internal int DiscoveryCalls { get; private set; }

        internal int SendCalls { get; private set; }

        internal TaskCompletionSource<bool>? SendStarted { get; init; }

        internal TaskCompletionSource<DiscordApiSendResult>? ControlledSend { get; init; }

        internal void ResetOperationCounts()
        {
            DiscoveryCalls = 0;
            SendCalls = 0;
        }

        public Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new DiscordBotIdentity(BotId, "Creator Toolkit bot", ApplicationId));

        public Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuild>>([new DiscordGuild(GuildId, "Creators")]);

        public Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
            string token,
            DiscordBotIdentity identity,
            string guildId,
            CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult<DiscordGuildDiscovery?>(new DiscordGuildDiscovery(
                new DiscordGuild(GuildId, "Creators"),
                [
                    new DiscordChannelCapability(ChannelOne, "first", 0, true, true, true, true, false),
                    new DiscordChannelCapability(ChannelTwo, "second", 0, true, true, true, true, false),
                ],
                [new DiscordRole(GuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false)],
                new DiscordGuildMember(BotId, "Creator Toolkit bot", [])));
        }

        public Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(
            string token,
            string guildId,
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuildMember>>([]);

        public Task<DiscordGuildMember?> GetMemberAsync(
            string token,
            string guildId,
            string userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(new DiscordGuildMember(userId, "Member", []));

        public async Task<DiscordApiSendResult> SendMessageAsync(
            string token,
            string channelId,
            DiscordMessageRequest request,
            DiscordValidatedImage? image,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            Messages.Add(request);
            if (image is not null)
            {
                Images.Add(image.Bytes.ToArray());
            }

            SendStarted?.TrySetResult(true);
            if (ControlledSend is not null)
            {
                return await ControlledSend.Task.WaitAsync(cancellationToken);
            }

            return Results.Count == 0
                ? new DiscordApiSendResult(DiscordDeliveryStatus.Success, "300000000000000099")
                : Results.Dequeue();
        }
    }
}
