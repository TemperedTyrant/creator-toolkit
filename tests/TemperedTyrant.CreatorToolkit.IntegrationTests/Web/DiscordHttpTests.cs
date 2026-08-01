using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AnnouncementHttpTests
{
    private const string DiscordTokenCanary = "discord-http-token-canary-9070cc4826ca47df";
    private const string DiscordGuildId = "400000000000000001";
    private const string DiscordChannelId = "400000000000000002";
    private const string DiscordSecondChannelId = "400000000000000006";

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
        Assert.Contains("data-image-trigger", ownerHtml, StringComparison.Ordinal);
        Assert.Contains(">Image</button>", ownerHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"UploadedImage\"", ownerHtml, StringComparison.Ordinal);
        Assert.Contains("Used for this publication only", ownerHtml, StringComparison.Ordinal);
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

    [Fact]
    public async Task PartialMemberSearchRequiresAuthorizationAndAntiforgeryWithoutLeakingQuery()
    {
        const string searchCanary = "member-search-query-canary-18e3b6";
        const string displayName = "<script>synthetic member</script>";
        List<string> logs = [];
        var api = new HttpDiscordApi { MemberDisplayName = displayName };
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        _ = await CreateAndActivateAsync(factory.Services, ownerId, "member-viewer", SystemRoles.Viewer);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);
        Guid connectionId;
        await using (AsyncServiceScope setup = factory.Services.CreateAsyncScope())
        {
            IDiscordConfigurationService configuration = setup.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>(
                (await configuration.CreateAsync("Discord", DiscordTokenCanary, ownerId)).Id);
            await configuration.SaveDestinationsAsync(
                connectionId,
                DiscordGuildId,
                [DiscordChannelId],
                ownerId);
        }

        using HttpClient owner = CreateClient(factory);
        using HttpClient viewer = CreateClient(factory);
        using HttpClient anonymous = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);
        await LoginAsync(viewer, "member-viewer", UserPassword);
        string pageRoute = $"/Announcements/{announcementId}/PublishDiscord"
            + $"?ConnectionId={connectionId}&GuildId={DiscordGuildId}";
        string composer = await owner.GetStringAsync(pageRoute);
        string handlerRoute = $"/Announcements/{announcementId}/PublishDiscord?handler=SearchMembers";
        using var request = new HttpRequestMessage(HttpMethod.Post, handlerRoute)
        {
            Content = Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(composer)),
                ("Id", announcementId.ToString()),
                ("ConnectionId", connectionId.ToString()),
                ("GuildId", DiscordGuildId),
                ("MemberQuery", searchCanary)),
        };
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Creator-Toolkit-Partial", "member-search");

        HttpResponseMessage response = await owner.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<script>", json, StringComparison.Ordinal);
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement member = Assert.Single(
                document.RootElement.GetProperty("members").EnumerateArray());
            Assert.Equal(displayName, member.GetProperty("displayName").GetString());
            Assert.Equal("400000000000000005", member.GetProperty("id").GetString());
        }

        Assert.Equal(searchCanary, Assert.Single(api.MemberQueries));
        Assert.DoesNotContain(searchCanary, request.RequestUri!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain(searchCanary, string.Join('\n', logs), StringComparison.Ordinal);

        using var missingAntiforgery = new HttpRequestMessage(HttpMethod.Post, handlerRoute)
        {
            Content = Form(
                ("Id", announcementId.ToString()),
                ("ConnectionId", connectionId.ToString()),
                ("GuildId", DiscordGuildId),
                ("MemberQuery", searchCanary)),
        };
        missingAntiforgery.Headers.Add("X-Creator-Toolkit-Partial", "member-search");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.SendAsync(missingAntiforgery)).StatusCode);

        using var viewerRequest = new HttpRequestMessage(HttpMethod.Post, handlerRoute)
        {
            Content = Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(await viewer.GetStringAsync("/ChangePassword"))),
                ("Id", announcementId.ToString()),
                ("ConnectionId", connectionId.ToString()),
                ("GuildId", DiscordGuildId),
                ("MemberQuery", searchCanary)),
        };
        AssertAccessDenied(await viewer.SendAsync(viewerRequest));

        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, handlerRoute)
        {
            Content = Form(("MemberQuery", searchCanary)),
        };
        AssertLoginRedirect(await anonymous.SendAsync(anonymousRequest));
    }

    [Fact]
    public async Task ReviewedUploadIsStagedOnceAndSentFreshToEverySelectedChannel()
    {
        const string byteCanary = "ephemeral-image-byte-canary-f8a32a";
        byte[] imageBytes =
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            .. Encoding.UTF8.GetBytes(byteCanary),
        ];
        List<string> logs = [];
        var api = new HttpDiscordApi();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                ServiceDescriptor? publicationWorker = services.SingleOrDefault(
                    value => value.ServiceType == typeof(IHostedService)
                        && value.ImplementationType == typeof(PublicationWorker));
                if (publicationWorker is not null)
                {
                    services.Remove(publicationWorker);
                }

                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);
        Guid connectionId;
        IReadOnlyList<Guid> destinationIds;
        await using (AsyncServiceScope setup = factory.Services.CreateAsyncScope())
        {
            IDiscordConfigurationService configuration = setup.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            connectionId = Assert.IsType<Guid>(
                (await configuration.CreateAsync("Discord", DiscordTokenCanary, ownerId)).Id);
            await configuration.SaveDestinationsAsync(
                connectionId,
                DiscordGuildId,
                [DiscordChannelId, DiscordSecondChannelId],
                ownerId);
            destinationIds = (await configuration.GetAsync(connectionId))!
                .Destinations
                .Select(value => value.Id)
                .Order()
                .ToArray();
        }

        using HttpClient owner = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);
        string route = $"/Announcements/{announcementId}/PublishDiscord"
            + $"?ConnectionId={connectionId}&GuildId={DiscordGuildId}";
        string composer = await owner.GetStringAsync(route);
        string submissionId = GetHiddenValue(composer, "SubmissionId");
        string revision = GetHiddenValue(composer, "AnnouncementRevision");
        string antiforgery = GetAntiforgeryToken(composer);

        using MultipartFormDataContent reviewForm = PublicationMultipartForm(
            announcementId,
            connectionId,
            submissionId,
            revision,
            destinationIds,
            antiforgery,
            imageBytes);
        HttpResponseMessage review = await owner.PostAsync(
            $"/Announcements/{announcementId}/PublishDiscord?handler=Review",
            reviewForm);
        string reviewHtml = await review.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Contains("Validated PNG image", reviewHtml, StringComparison.Ordinal);
        Assert.Contains($"{imageBytes.Length} bytes", reviewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(byteCanary, reviewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadHandle", reviewHtml, StringComparison.Ordinal);
        string reviewToken = GetHiddenValue(reviewHtml, "ReviewToken");
        Assert.DoesNotContain(byteCanary, reviewToken, StringComparison.Ordinal);

        FormUrlEncodedContent publishForm = PublicationForm(
            announcementId,
            connectionId,
            submissionId,
            revision,
            destinationIds,
            GetAntiforgeryToken(reviewHtml),
            reviewToken);
        HttpResponseMessage published = await owner.PostAsync(
            $"/Announcements/{announcementId}/PublishDiscord?handler=Publish",
            publishForm);

        Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);
        Assert.StartsWith("/PublishHistory/", published.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Empty(api.SentImages);
        await using (AsyncServiceScope firstDelivery = factory.Services.CreateAsyncScope())
        {
            Assert.True(await firstDelivery.ServiceProvider
                .GetRequiredService<PublicationProcessor>()
                .ProcessNextAsync("http-test-worker", CancellationToken.None));
        }
        await using (AsyncServiceScope secondDelivery = factory.Services.CreateAsyncScope())
        {
            Assert.True(await secondDelivery.ServiceProvider
                .GetRequiredService<PublicationProcessor>()
                .ProcessNextAsync("http-test-worker", CancellationToken.None));
        }

        Assert.Equal(2, api.SentImages.Count);
        Assert.All(api.SentImages, sent => Assert.Equal(imageBytes, sent));
        Assert.NotSame(api.SentImages[0], api.SentImages[1]);

        FormUrlEncodedContent duplicateForm = PublicationForm(
            announcementId,
            connectionId,
            submissionId,
            revision,
            destinationIds,
            GetAntiforgeryToken(reviewHtml),
            reviewToken);
        HttpResponseMessage duplicate = await owner.PostAsync(
            $"/Announcements/{announcementId}/PublishDiscord?handler=Publish",
            duplicateForm);
        string duplicateHtml = await duplicate.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Redirect, duplicate.StatusCode);
        Assert.Equal(published.Headers.Location, duplicate.Headers.Location);
        Assert.Equal(2, api.SentImages.Count);
        Assert.DoesNotContain(byteCanary, duplicateHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(byteCanary, duplicate.Headers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(byteCanary, string.Join('\n', logs), StringComparison.Ordinal);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext database = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.DoesNotContain(
            byteCanary,
            string.Join(
                '\n',
                await database.AuditRecords
                    .Select(value => value.EventCode + value.Outcome + value.ReasonCode)
                    .ToListAsync()),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            byteCanary,
            string.Join(
                '\n',
                await database.DiagnosticRecords
                    .Select(value => value.Reference + value.ErrorCode + value.Operation + value.ExceptionType)
                    .ToListAsync()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishHistoryIsContentFreeAndViewerCannotCancelByDirectPost()
    {
        const string queuedContentCanary = "history-content-canary-b1f587035645";
        var api = new HttpDiscordApi();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                ServiceDescriptor? publicationWorker = services.SingleOrDefault(
                    value => value.ServiceType == typeof(IHostedService)
                        && value.ImplementationType == typeof(PublicationWorker));
                if (publicationWorker is not null)
                {
                    services.Remove(publicationWorker);
                }

                services.RemoveAll<IDiscordApi>();
                services.AddSingleton<IDiscordApi>(api);
            });
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        _ = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "history-viewer",
            SystemRoles.Viewer);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);
        Guid publicationId;
        await using (AsyncServiceScope setup = factory.Services.CreateAsyncScope())
        {
            IDiscordConfigurationService configuration = setup.ServiceProvider
                .GetRequiredService<IDiscordConfigurationService>();
            Guid connectionId = Assert.IsType<Guid>((await configuration.CreateAsync(
                "Discord",
                DiscordTokenCanary,
                ownerId)).Id);
            await configuration.SaveDestinationsAsync(
                connectionId,
                DiscordGuildId,
                [DiscordChannelId],
                ownerId);
            DiscordDestinationListItem destination = Assert.Single(
                (await configuration.GetAsync(connectionId))!.Destinations);
            DiscordPublicationEnqueueResult queued = await setup.ServiceProvider
                .GetRequiredService<IDiscordPublishingService>()
                .EnqueueAsync(
                    new DiscordPublishRequest(
                        Guid.NewGuid(),
                        announcementId,
                        1,
                        connectionId,
                        DiscordGuildId,
                        [destination.Id],
                        DiscordMessageMode.Plain,
                        queuedContentCanary,
                        false,
                        null,
                        DiscordMentionSelection.None,
                        false,
                        null,
                        null),
                    false,
                    ownerId);
            publicationId = queued.PublicationId;
        }

        using HttpClient owner = CreateClient(factory);
        using HttpClient viewer = CreateClient(factory);
        await LoginAsync(owner, "owner-local", OwnerPassword);
        await LoginAsync(viewer, "history-viewer", UserPassword);
        string route = $"/PublishHistory/{publicationId}";
        HttpResponseMessage viewerPage = await viewer.GetAsync(route);
        string viewerHtml = await viewerPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, viewerPage.StatusCode);
        Assert.Contains("no-store", viewerPage.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.False(
            viewerHtml.Contains(queuedContentCanary, StringComparison.Ordinal),
            "The queued-content canary appeared in publication history.");
        Assert.DoesNotContain("Cancel remaining sends", viewerHtml, StringComparison.Ordinal);

        using var viewerCancel = new HttpRequestMessage(
            HttpMethod.Post,
            route + "?handler=Cancel")
        {
            Content = Form(
                ("Id", publicationId.ToString()),
                ("Revision", "1"),
                ("__RequestVerificationToken", GetAntiforgeryToken(viewerHtml))),
        };
        AssertAccessDenied(await viewer.SendAsync(viewerCancel));

        string ownerHtml = await owner.GetStringAsync(route);
        using var ownerCancel = new HttpRequestMessage(
            HttpMethod.Post,
            route + "?handler=Cancel")
        {
            Content = Form(
                ("Id", publicationId.ToString()),
                ("Revision", "1"),
                ("__RequestVerificationToken", GetAntiforgeryToken(ownerHtml))),
        };
        HttpResponseMessage cancelled = await owner.SendAsync(ownerCancel);
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.StartsWith(route, cancelled.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    private static MultipartFormDataContent PublicationMultipartForm(
        Guid announcementId,
        Guid connectionId,
        string submissionId,
        string revision,
        IReadOnlyList<Guid> destinationIds,
        string antiforgery,
        byte[] imageBytes)
    {
        var form = new MultipartFormDataContent();
        AddPublicationFields(
            (name, value) => form.Add(new StringContent(value), name),
            announcementId,
            connectionId,
            submissionId,
            revision,
            destinationIds,
            antiforgery);
        var image = new ByteArrayContent(imageBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "UploadedImage", "browser-local-name.png");
        return form;
    }

    private static FormUrlEncodedContent PublicationForm(
        Guid announcementId,
        Guid connectionId,
        string submissionId,
        string revision,
        IReadOnlyList<Guid> destinationIds,
        string antiforgery,
        string reviewToken)
    {
        List<KeyValuePair<string, string>> fields = [];
        AddPublicationFields(
            (name, value) => fields.Add(new KeyValuePair<string, string>(name, value)),
            announcementId,
            connectionId,
            submissionId,
            revision,
            destinationIds,
            antiforgery);
        fields.Add(new KeyValuePair<string, string>("ReviewComplete", "true"));
        fields.Add(new KeyValuePair<string, string>("ReviewToken", reviewToken));
        fields.Add(new KeyValuePair<string, string>("FinalConfirmation", "true"));
        return new FormUrlEncodedContent(fields);
    }

    private static void AddPublicationFields(
        Action<string, string> add,
        Guid announcementId,
        Guid connectionId,
        string submissionId,
        string revision,
        IReadOnlyList<Guid> destinationIds,
        string antiforgery)
    {
        add("__RequestVerificationToken", antiforgery);
        add("Id", announcementId.ToString());
        add("ConnectionId", connectionId.ToString());
        add("GuildId", DiscordGuildId);
        add("SubmissionId", submissionId);
        add("AnnouncementRevision", revision);
        foreach (Guid destinationId in destinationIds)
        {
            add("DestinationIds", destinationId.ToString());
        }

        add("Mode", DiscordMessageMode.Plain.ToString());
        add("PlainContent", "Synthetic foreground publication");
        add("ShowLinkPreviews", "true");
        add("ImageAltText", "Synthetic image");
        add("ImageSpoiler", "true");
    }

    private sealed class HttpDiscordApi(string guildName = "Creators") : IDiscordApi
    {
        internal List<DiscordMessageRequest> SentMessages { get; } = [];

        internal List<byte[]> SentImages { get; } = [];

        internal List<string> MemberQueries { get; } = [];

        internal string MemberDisplayName { get; set; } = "Member";

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
                [
                    new DiscordChannelCapability(DiscordChannelId, "announcements", 0, true, true, true, true, true),
                    new DiscordChannelCapability(DiscordSecondChannelId, "updates", 0, true, true, true, true, true),
                ],
                [new DiscordRole(DiscordGuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false)],
                new DiscordGuildMember(identity.BotUserId, "Creator Toolkit bot", [])));
        }

        public Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(string token, string guildId, string query, CancellationToken cancellationToken)
        {
            MemberQueries.Add(query);
            return Task.FromResult<IReadOnlyList<DiscordGuildMember>>(
                [new DiscordGuildMember("400000000000000005", MemberDisplayName, [])]);
        }

        public Task<DiscordGuildMember?> GetMemberAsync(string token, string guildId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(new DiscordGuildMember(userId, "Member", []));

        public Task<DiscordApiSendResult> SendMessageAsync(string token, string channelId, DiscordMessageRequest request, IReadOnlyList<DiscordValidatedImage> images, CancellationToken cancellationToken)
        {
            SentMessages.Add(request);
            if (images.Count > 0)
            {
                SentImages.Add(images[0].Bytes.ToArray());
            }

            return Task.FromResult(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "400000000000000099"));
        }
    }
}
