using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AnnouncementHttpTests
{
    private const string DiscordTokenCanary = "discord-http-token-canary-9070cc4826ca47df";
    private const string DiscordGuildId = "400000000000000001";
    private const string DiscordChannelId = "400000000000000002";

    [Fact]
    public async Task DiscordPagesEnforceRolePoliciesAndNeverRedisplaySubmittedToken()
    {
        List<string> logs = [];
        var api = new HttpDiscordApi();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        _ = await CreateAndActivateAsync(factory.Services, ownerId, "discord-admin", SystemRoles.Admin);
        CreatedUser editorUser = await CreateAndActivateAsync(factory.Services, ownerId, "discord-editor", SystemRoles.Editor);
        CreatedUser viewerUser = await CreateAndActivateAsync(factory.Services, ownerId, "discord-viewer", SystemRoles.Viewer);
        using HttpClient owner = CreateClient(factory);
        using HttpClient admin = CreateClient(factory);
        using HttpClient editor = CreateClient(factory);
        using HttpClient viewer = CreateClient(factory);
        using HttpClient anonymous = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);
        await LoginAsync(admin, "discord-admin", UserPassword);
        await LoginAsync(editor, "discord-editor", UserPassword);
        await LoginAsync(viewer, "discord-viewer", UserPassword);

        string newHtml = await owner.GetStringAsync("/Destinations/Discord/New");
        HttpResponseMessage created = await owner.PostAsync(
            "/Destinations/Discord/New",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(newHtml)),
                ("Name", "Dedicated Creator Toolkit bot"),
                ("BotToken", DiscordTokenCanary)));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.False(
            created.Headers.ToString().Contains(DiscordTokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in redirect headers.");
        Uri detailsLocation = Assert.IsType<Uri>(created.Headers.Location);
        string detailsHtml = await owner.GetStringAsync(detailsLocation);
        Assert.False(
            detailsHtml.Contains(DiscordTokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in the connection-details HTML.");
        Assert.Contains("Configured (encrypted; never displayed)", detailsHtml, StringComparison.Ordinal);
        Assert.Contains("permissions=52224", detailsHtml, StringComparison.Ordinal);
        Assert.Contains("scope=bot", detailsHtml, StringComparison.Ordinal);
        Guid connectionId = Guid.Parse(
            detailsLocation.OriginalString.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]
                .Split('?')[0]);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Destinations/Discord/New")).StatusCode);
        AssertAccessDenied(await editor.GetAsync("/Destinations/Discord/New"));
        AssertAccessDenied(await viewer.GetAsync("/Destinations/Discord/New"));
        AssertLoginRedirect(await anonymous.GetAsync("/Destinations"));

        string editorDetails = await editor.GetStringAsync($"/Destinations/Discord/{connectionId}");
        string viewerDetails = await viewer.GetStringAsync($"/Destinations/Discord/{connectionId}");
        Assert.DoesNotContain("Replace bot token", editorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete Discord connection", editorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("Replace bot token", viewerDetails, StringComparison.Ordinal);

        string editorToken = GetAntiforgeryToken(await editor.GetStringAsync("/ChangePassword"));
        AssertAccessDenied(await editor.PostAsync(
            $"/Destinations/Discord/{connectionId}?handler=ReplaceToken",
            Form(
                ("__RequestVerificationToken", editorToken),
                ("revision", "1"),
                ("BotToken", DiscordTokenCanary))));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.PostAsync(
                $"/Destinations/Discord/{connectionId}?handler=Delete",
                Form(("revision", "1"), ("confirmed", "true")))).StatusCode);

        string viewerStamp = await GetConcurrencyStampAsync(factory.Services, viewerUser.Id);
        await using (AsyncServiceScope disabledScope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await disabledScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DisableAsync(ownerId, viewerUser.Id, viewerStamp)).Status);
        }

        AssertLoginRedirect(await viewer.GetAsync($"/Destinations/Discord/{connectionId}"));

        await using (AsyncServiceScope stampScope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = stampScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser staleEditor = (await users.FindByIdAsync(editorUser.Id.ToString()))!;
            Assert.True((await users.UpdateSecurityStampAsync(staleEditor)).Succeeded);
        }

        AssertLoginRedirect(await editor.GetAsync($"/Destinations/Discord/{connectionId}"));

        string unsafeDestinations = created.Headers + string.Join('\n', logs);
        Assert.False(
            unsafeDestinations.Contains(DiscordTokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in captured HTTP headers or logs.");
        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(
            (await db.ProtectedSecrets.Select(value => value.Ciphertext).SingleAsync())
                .Contains(DiscordTokenCanary, StringComparison.Ordinal),
            "The bot-token canary appeared in database plaintext.");
    }

    [Fact]
    public async Task DraftPublishingPageAllowsEditorsButNotViewersAndMassMentionsRemainPrivileged()
    {
        var api = new HttpDiscordApi();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        _ = await CreateAndActivateAsync(factory.Services, ownerId, "publish-editor", SystemRoles.Editor);
        _ = await CreateAndActivateAsync(factory.Services, ownerId, "publish-viewer", SystemRoles.Viewer);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);
        Guid connectionId;
        await using (AsyncServiceScope setup = factory.Services.CreateAsyncScope())
        {
            IDiscordConfigurationService configuration = setup.ServiceProvider.GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>((await configuration.CreateAsync("Discord", DiscordTokenCanary, ownerId)).Id);
            await configuration.SaveDestinationsAsync(connectionId, DiscordGuildId, [DiscordChannelId], ownerId);
        }

        using HttpClient owner = CreateClient(factory);
        using HttpClient editor = CreateClient(factory);
        using HttpClient viewer = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);
        await LoginAsync(editor, "publish-editor", UserPassword);
        await LoginAsync(viewer, "publish-viewer", UserPassword);

        string ownerDetails = await owner.GetStringAsync($"/Announcements/{announcementId}");
        string editorDetails = await editor.GetStringAsync($"/Announcements/{announcementId}");
        string viewerDetails = await viewer.GetStringAsync($"/Announcements/{announcementId}");
        Assert.Contains("Publish to Discord", ownerDetails, StringComparison.Ordinal);
        Assert.Contains("Publish to Discord", editorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish to Discord", viewerDetails, StringComparison.Ordinal);

        string route = $"/Announcements/{announcementId}/PublishDiscord?ConnectionId={connectionId}&GuildId={DiscordGuildId}";
        HttpResponseMessage ownerPage = await owner.GetAsync(route);
        HttpResponseMessage editorPage = await editor.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, ownerPage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editorPage.StatusCode);
        AssertNoStoreAndSecurityHeaders(ownerPage);
        string ownerHtml = await ownerPage.Content.ReadAsStringAsync();
        string editorHtml = await editorPage.Content.ReadAsStringAsync();
        Assert.Contains("MentionEveryone", ownerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("MentionEveryone", editorHtml, StringComparison.Ordinal);
        AssertAccessDenied(await viewer.GetAsync(route));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await editor.PostAsync(
                $"/Announcements/{announcementId}/PublishDiscord?handler=Publish",
                Form(("Id", announcementId.ToString())))).StatusCode);
        Assert.Empty(api.SentMessages);
    }

    [Fact]
    public async Task DiscoveryProcessingFailureUsesSafeCategoryStageAndDiagnosticReference()
    {
        const string guildNameCanary = "guild-name-canary-never-log";
        List<string> logs = [];
        var api = new HttpDiscordApi(guildNameCanary);
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        Guid connectionId;
        await using (AsyncServiceScope setup = factory.Services.CreateAsyncScope())
        {
            DiscordOperationResult created = await setup.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>()
                .CreateAsync("Synthetic Discord", DiscordTokenCanary, ownerId);
            connectionId = Assert.IsType<Guid>(created.Id);
        }

        api.DiscoveryFailure = new DiscordServerInformationException(
            DiscordDiscoveryStage.ChannelListDeserialization,
            DiscordServerInformationFailure.UnsupportedResponse);
        using HttpClient owner = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);

        HttpResponseMessage response = await owner.GetAsync(
            $"/Destinations/Discord/{connectionId}?GuildId={DiscordGuildId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Discord returned unsupported server information.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Validate the bot credential", html, StringComparison.Ordinal);
        Assert.Matches("CTK-[A-F0-9]{32}", html);
        string capturedLogs = string.Join('\n', logs);
        Assert.Contains("Stage: ChannelListDeserialization", capturedLogs, StringComparison.Ordinal);
        Assert.Contains("category: UnsupportedResponse", capturedLogs, StringComparison.Ordinal);
        Assert.Contains(connectionId.ToString(), capturedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DiscordGuildId, capturedLogs, StringComparison.Ordinal);
        Assert.False(
            capturedLogs.Contains(guildNameCanary, StringComparison.Ordinal),
            "The synthetic guild-name canary appeared in captured diagnostics.");
        Assert.False(
            capturedLogs.Contains(DiscordTokenCanary, StringComparison.Ordinal),
            "The synthetic token canary appeared in captured diagnostics.");

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        DiagnosticRecord record = await verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .DiagnosticRecords
            .SingleAsync(value => value.Operation == "discord-server-discovery");
        Assert.Equal("infrastructure", record.Category);
        Assert.Equal("invalid-operation", record.ExceptionType);
        Assert.DoesNotContain(guildNameCanary, record.Reference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiscordServerInformationFailure.AuthenticationFailed, "Discord bot authentication failed.")]
    [InlineData(DiscordServerInformationFailure.NotInstalled, "The bot is no longer installed in this Discord server.")]
    [InlineData(DiscordServerInformationFailure.AccessDenied, "Discord denied access to server information.")]
    [InlineData(DiscordServerInformationFailure.UnsupportedResponse, "Discord returned unsupported server information.")]
    [InlineData(DiscordServerInformationFailure.TemporarilyUnavailable, "Discord is temporarily unavailable.")]
    [InlineData(DiscordServerInformationFailure.ProcessingFailed, "Discord server information could not be processed.")]
    public void DiscoveryFailuresHaveDistinctFixedSafeMessages(
        DiscordServerInformationFailure failure,
        string expected)
    {
        var exception = new DiscordServerInformationException(
            DiscordDiscoveryStage.GuildResponse,
            failure);

        Assert.Equal(expected, exception.SafeMessage);
    }

    private sealed class HttpDiscordApi(string guildName = "Creators") : IDiscordApi
    {
        internal List<DiscordMessageRequest> SentMessages { get; } = [];

        internal Exception? DiscoveryFailure { get; set; }

        public Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new DiscordBotIdentity("400000000000000003", "Creator Toolkit bot", "400000000000000004"));

        public Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuild>>([new DiscordGuild(DiscordGuildId, guildName)]);

        public Task<DiscordGuildDiscovery?> DiscoverGuildAsync(string token, DiscordBotIdentity identity, string guildId, CancellationToken cancellationToken)
        {
            if (DiscoveryFailure is not null)
            {
                return Task.FromException<DiscordGuildDiscovery?>(DiscoveryFailure);
            }

            return Task.FromResult<DiscordGuildDiscovery?>(new DiscordGuildDiscovery(
                new DiscordGuild(DiscordGuildId, guildName),
                [new DiscordChannelCapability(DiscordChannelId, "announcements", 0, true, true, true, true, true)],
                [new DiscordRole(DiscordGuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false)],
                new DiscordGuildMember(identity.BotUserId, "Creator Toolkit bot", [])));
        }

        public Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(string token, string guildId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuildMember>>([]);

        public Task<DiscordGuildMember?> GetMemberAsync(string token, string guildId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(new DiscordGuildMember(userId, "Member", []));

        public Task<DiscordApiSendResult> SendMessageAsync(string token, string channelId, DiscordMessageRequest request, DiscordValidatedImage? image, CancellationToken cancellationToken)
        {
            SentMessages.Add(request);
            return Task.FromResult(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "400000000000000099"));
        }
    }
}
