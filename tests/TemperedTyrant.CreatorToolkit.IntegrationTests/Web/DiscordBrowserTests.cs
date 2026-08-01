using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Discord;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AnnouncementBrowserTests
{
    private const string DiscordBrowserToken = "browser-controlled-discord-token-31bd96eecaeb";
    private const string DiscordBrowserGuildId = "500000000000000001";
    private const string DiscordBrowserChannelOne = "500000000000000002";
    private const string DiscordBrowserChannelTwo = "500000000000000003";
    private const string DiscordBrowserRoleId = "500000000000000004";
    private const string DiscordBrowserMemberId = "500000000000000005";

    [Fact]
    public async Task ControlledDiscordAdapterCompletesSetupCompositionDeliveryAndRoleJourney()
    {
        using TestDataDirectory data = new();
        await InitializeAccountsAsync(data.Path);
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}");
        var adapter = new BrowserDiscordApi();
        await using BrowserDiscordWebHost host = await BrowserDiscordWebHost.StartAsync(
            FindRepositoryRoot(),
            data.Path,
            origin,
            adapter);
        Guid announcementId;
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            Guid ownerId = await scope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .Users
                .Where(value => value.UserName == "owner-local")
                .Select(value => value.Id)
                .SingleAsync();
            UserLifecycleResult pending = await scope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .CreatePendingAsync(ownerId, "discord-editor-browser", "Editor", SystemRoles.Editor);
            await scope.ServiceProvider
                .GetRequiredService<AccountActivationService>()
                .ActivateAsync(pending.OneTimeActivationCapability!, ViewerPassword);
            announcementId = Guid.NewGuid();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await scope.ServiceProvider.GetRequiredService<IAnnouncementService>().CreateAsync(
                    announcementId,
                    "Browser Discord draft",
                    "Browser Discord body",
                    ownerId)).Status);
        }

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await LoginAsync(page, origin, "owner-local", OwnerPassword);

        await page.GetByRole(AriaRole.Link, new() { Name = "Destinations", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Add Discord bot" }).ClickAsync();
        await page.Locator("#Name").FillAsync("Dedicated Creator Toolkit bot");
        await page.Locator("#BotToken").FillAsync(DiscordBrowserToken);
        await Task.WhenAll(
            page.WaitForURLAsync("**/Destinations/Discord/*?notice=created"),
            page.GetByRole(AriaRole.Button, new() { Name = "Validate and save" }).ClickAsync());
        adapter.ConnectionId = Guid.Parse(new Uri(page.Url).AbsolutePath.Split('/').Last());
        Assert.Equal(string.Empty, await page.Locator("#BotToken").InputValueAsync());
        Assert.True(await page.GetByRole(AriaRole.Link, new() { Name = "Install bot in Discord" }).IsVisibleAsync());

        await page.Locator("#GuildId").SelectOptionAsync(DiscordBrowserGuildId);
        await page.GetByRole(AriaRole.Button, new() { Name = "View usable channels" }).ClickAsync();
        await page.Locator($"input[name='ChannelIds'][value='{DiscordBrowserChannelOne}']").CheckAsync();
        await page.Locator($"input[name='ChannelIds'][value='{DiscordBrowserChannelTwo}']").CheckAsync();
        await Task.WhenAll(
            page.WaitForURLAsync("**notice=updated"),
            page.GetByRole(AriaRole.Button, new() { Name = "Save selected channels" }).ClickAsync());
        Assert.True(await page.GetByRole(AriaRole.Heading, new() { Name = "Creators · #announcements" }).IsVisibleAsync());
        Assert.True(await page.GetByRole(AriaRole.Heading, new() { Name = "Creators · #updates" }).IsVisibleAsync());

        await SignOutAsync(page);
        await LoginAsync(page, origin, "discord-editor-browser", ViewerPassword);
        await page.GotoAsync(new Uri(origin, $"/Announcements/{announcementId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Link, new() { Name = "Publish to Discord" }).ClickAsync();
        Assert.Equal(0, await page.Locator("#MentionEveryone").CountAsync());
        await page.Locator("#ConnectionId").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Load publishing options" }).ClickAsync();
        await page.Locator("#GuildId").SelectOptionAsync(DiscordBrowserGuildId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Load publishing options" }).ClickAsync();
        await page.Locator("input[name='DestinationIds']").First.CheckAsync();
        await page.Locator("input[name='Mode'][value='Embed']").CheckAsync();
        await page.Locator("#EmbedMessageText").FillAsync("**Durable browser send**");
        await page.Locator("#EmbedTitle").FillAsync("Discord-specific title");
        await page.Locator("#EmbedDescription").FillAsync("Role and member selected explicitly.");
        await page.Locator("#EmbedTitleUrl").FillAsync("https://example.invalid/title");
        await page.Locator("#EmbedColor").FillAsync("#74c7a5");
        await page.Locator("#EmbedFooter").FillAsync("Synthetic footer");
        await page.Locator("#EmbedThumbnailUrl").FillAsync("https://example.invalid/thumbnail.png");
        await page.Locator("#RemoteImageUrl").FillAsync("https://example.invalid/preserved-only.png");
        await page.Locator($"input[name='RoleIds'][value='{DiscordBrowserRoleId}']").CheckAsync();
        await page.Locator("#ImageAltText").FillAsync("Synthetic image");
        await page.Locator("#ImageSpoiler").CheckAsync();
        await page.Locator("#ImageInEmbed").CheckAsync();
        var image = new FilePayload
        {
            Name = "synthetic.png",
            MimeType = "image/png",
            Buffer = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00],
        };
        await page.Locator("#UploadedImage").SetInputFilesAsync(image);
        string composerUrl = page.Url;
        string announcementRevision = await page.Locator("#AnnouncementRevision").InputValueAsync();
        string submissionId = await page.Locator("#SubmissionId").InputValueAsync();
        await page.Locator("#MemberQuery").FillAsync("older");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search up to 25 members" }).ClickAsync();
        await adapter.OlderSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Locator("#MemberQuery").FillAsync("newer");
        Task<IResponse> newerSearchResponse = page.WaitForResponseAsync(
            response => response.Request.Method == "POST"
                && response.Url.Contains("handler=SearchMembers", StringComparison.Ordinal)
                && response.Ok);
        await page.GetByRole(AriaRole.Button, new() { Name = "Search up to 25 members" }).ClickAsync();
        await newerSearchResponse;
        adapter.OlderSearchCompletion.TrySetResult(
            [new DiscordGuildMember("500000000000000010", "Older result", [])]);
        Assert.True(await page.GetByText("Newer result", new() { Exact = false }).IsVisibleAsync());
        Assert.Equal(0, await page.GetByText("Older result", new() { Exact = false }).CountAsync());
        await page.Locator("#MemberQuery").FillAsync("member");
        Task<IResponse> memberSearchResponse = page.WaitForResponseAsync(
            response => response.Request.Method == "POST"
                && response.Url.Contains("handler=SearchMembers", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Search up to 25 members" }).ClickAsync();
        Assert.True((await memberSearchResponse).Ok);
        Assert.Equal(composerUrl, page.Url);
        Assert.Equal("**Durable browser send**", await page.Locator("#EmbedMessageText").InputValueAsync());
        Assert.Equal("Discord-specific title", await page.Locator("#EmbedTitle").InputValueAsync());
        Assert.Equal("Role and member selected explicitly.", await page.Locator("#EmbedDescription").InputValueAsync());
        Assert.Equal("https://example.invalid/title", await page.Locator("#EmbedTitleUrl").InputValueAsync());
        Assert.Equal("#74c7a5", await page.Locator("#EmbedColor").InputValueAsync());
        Assert.Equal("Synthetic footer", await page.Locator("#EmbedFooter").InputValueAsync());
        Assert.Equal("https://example.invalid/thumbnail.png", await page.Locator("#EmbedThumbnailUrl").InputValueAsync());
        Assert.Equal("https://example.invalid/preserved-only.png", await page.Locator("#RemoteImageUrl").InputValueAsync());
        Assert.Equal(announcementRevision, await page.Locator("#AnnouncementRevision").InputValueAsync());
        Assert.Equal(submissionId, await page.Locator("#SubmissionId").InputValueAsync());
        Assert.True(await page.Locator("input[name='Mode'][value='Embed']").IsCheckedAsync());
        Assert.True(await page.Locator("input[name='DestinationIds']").First.IsCheckedAsync());
        Assert.True(await page.Locator($"input[name='RoleIds'][value='{DiscordBrowserRoleId}']").IsCheckedAsync());
        Assert.True(await page.Locator("#ImageSpoiler").IsCheckedAsync());
        Assert.True(await page.Locator("#ImageInEmbed").IsCheckedAsync());
        Assert.Equal(1, await page.Locator("#UploadedImage").EvaluateAsync<int>("input => input.files.length"));
        await page.Locator($"input[data-member-id='{DiscordBrowserMemberId}']").CheckAsync();
        Assert.Equal(
            1,
            await page.Locator($"input[type='hidden'][name='UserIds'][value='{DiscordBrowserMemberId}']").CountAsync());
        await page.Locator("#RemoteImageUrl").FillAsync(string.Empty);
        await page.GetByRole(AriaRole.Button, new() { Name = "Review publication", Exact = true }).ClickAsync();
        Assert.True(
            await page.GetByRole(AriaRole.Heading, new() { Name = "4. Confirmation" }).IsVisibleAsync(),
            page.Url + ": " + string.Join(
                " | ",
                await page.Locator("[role='alert'], .field-validation-error").AllTextContentsAsync()));
        Assert.True(await page.GetByText("Validated PNG image", new() { Exact = false }).IsVisibleAsync());
        Assert.Equal(0, await page.Locator("#UploadedImage").EvaluateAsync<int>("input => input.files.length"));
        await page.Locator("#FinalConfirmation").CheckAsync();
        await Task.WhenAll(
            page.WaitForURLAsync("**/PublishHistory/**"),
            page.GetByRole(AriaRole.Button, new() { Name = "Queue Discord publication" }).ClickAsync());
        await adapter.SendCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await page.ReloadAsync();
        Assert.True(await page.GetByText("Succeeded", new() { Exact = true }).First.IsVisibleAsync());
        string historyHtml = await page.ContentAsync();
        Assert.DoesNotContain("Durable browser send", historyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic image", historyHtml, StringComparison.Ordinal);
        Assert.Single(adapter.Sent);
        Assert.True(adapter.Sent[0].Spoiler);
        Assert.True(adapter.Sent[0].EmbedPlacement);
        Assert.Equal(image.Buffer, adapter.Sent[0].ImageBytes);
        Assert.Contains(DiscordBrowserRoleId, adapter.Sent[0].Request.AllowedMentions.Roles!);
        Assert.Contains(DiscordBrowserMemberId, adapter.Sent[0].Request.AllowedMentions.Users!);

        await SignOutAsync(page);
        await LoginAsync(page, origin, "announcement-viewer", ViewerPassword);
        await page.GotoAsync(new Uri(origin, $"/Announcements/{announcementId}").AbsoluteUri);
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Publish to Discord" }).CountAsync());
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => sessionStorage.length"));

        await SignOutAsync(page);
        await LoginAsync(page, origin, "owner-local", OwnerPassword);
        await page.GotoAsync(
            new Uri(
                origin,
                $"/Announcements/{announcementId}/PublishDiscord?ConnectionId={adapter.ConnectionId}&GuildId={DiscordBrowserGuildId}").AbsoluteUri);
        await page.Locator("input[name='DestinationIds']").First.CheckAsync();
        Assert.True(await page.GetByText("High impact:", new() { Exact = false }).IsVisibleAsync());
        await page.Locator("#MentionEveryone").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Review publication", Exact = true }).ClickAsync();
        Assert.True(await page.GetByText("Mass mentions require the high-impact confirmation.", new() { Exact = true }).IsVisibleAsync());
        Assert.Single(adapter.Sent);
        await page.Locator("#MassMentionConfirmed").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Review publication", Exact = true }).ClickAsync();
        Assert.True(await page.GetByRole(AriaRole.Heading, new() { Name = "4. Confirmation" }).IsVisibleAsync());
    }

    private sealed class BrowserDiscordWebHost(WebApplication application) : IAsyncDisposable
    {
        internal IServiceProvider Services => application.Services;

        internal static async Task<BrowserDiscordWebHost> StartAsync(
            string repositoryRoot,
            string dataDirectory,
            Uri origin,
            IDiscordApi adapter)
        {
            string contentRoot = Path.Combine(repositoryRoot, "src", "TemperedTyrant.CreatorToolkit.Web");
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    ApplicationName = typeof(Program).Assembly.FullName,
                    ContentRootPath = contentRoot,
                    EnvironmentName = "Production",
                });
            builder.WebHost.UseUrls(origin.AbsoluteUri);
            builder.Services.AddCreatorToolkitInfrastructure(dataDirectory);
            builder.Services.RemoveAll<IDiscordApi>();
            builder.Services.AddSingleton(adapter);
            builder.Services.AddRazorPages();
            builder.Services.AddSingleton<DiscordEphemeralUploadStore>();
            builder.Services.AddHostedService<PublicationWorker>();
            builder.Services
                .AddAuthentication(
                    options =>
                    {
                        options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                    })
                .AddIdentityCookies();
            builder.Services.ConfigureApplicationCookie(
                options =>
                {
                    options.LoginPath = "/Login";
                    options.AccessDeniedPath = "/AccessDenied";
                    options.EventsType = typeof(CreatorToolkitCookieEvents);
                });
            builder.Services.AddScoped<CreatorToolkitCookieEvents>();
            builder.Services.Configure<SecurityStampValidatorOptions>(
                options => options.ValidationInterval = TimeSpan.Zero);
            builder.Services.AddAuthorization(
                options =>
                {
                    options.AddPolicy(AuthorizationPolicies.Administration, policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin));
                    options.AddPolicy(AuthorizationPolicies.ContentEditing, policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin, SystemRoles.Editor));
                    options.AddPolicy(AuthorizationPolicies.ApplicationAccess, policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin, SystemRoles.Editor, SystemRoles.Viewer));
                    options.AddPolicy(AuthorizationPolicies.OwnerOnly, policy => policy.RequireRole(SystemRoles.Owner));
                    options.AddPolicy(AuthorizationPolicies.ManageUsers, policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin));
                    options.AddPolicy(AuthorizationPolicies.TransferOwnership, policy => policy.RequireRole(SystemRoles.Owner));
                    options.FallbackPolicy = options.GetPolicy(AuthorizationPolicies.ApplicationAccess);
                });

            WebApplication app = builder.Build();
            await app.Services.GetRequiredService<PersistenceInitializer>().InitializeAsync();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRazorPages();
            await app.StartAsync();
            return new BrowserDiscordWebHost(app);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class BrowserDiscordApi : IDiscordApi
    {
        internal List<SentDiscordMessage> Sent { get; } = [];

        internal TaskCompletionSource<bool> OlderSearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<IReadOnlyList<DiscordGuildMember>> OlderSearchCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Guid ConnectionId { get; set; }

        internal TaskCompletionSource<bool> SendCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new DiscordBotIdentity("500000000000000006", "Creator Toolkit bot", "500000000000000007"));

        public Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordGuild>>([new DiscordGuild(DiscordBrowserGuildId, "Creators")]);

        public Task<DiscordGuildDiscovery?> DiscoverGuildAsync(string token, DiscordBotIdentity identity, string guildId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildDiscovery?>(new DiscordGuildDiscovery(
                new DiscordGuild(DiscordBrowserGuildId, "Creators"),
                [
                    new DiscordChannelCapability(DiscordBrowserChannelOne, "announcements", 0, true, true, true, true, true),
                    new DiscordChannelCapability(DiscordBrowserChannelTwo, "updates", 5, true, true, true, true, true),
                ],
                [
                    new DiscordRole(DiscordBrowserGuildId, "everyone", DiscordPermissionCalculator.StandardInstallPermissions.ToString(CultureInfo.InvariantCulture), false),
                    new DiscordRole(DiscordBrowserRoleId, "Supporters", "0", true),
                ],
                new DiscordGuildMember(identity.BotUserId, "Creator Toolkit bot", [])));

        public async Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(string token, string guildId, string query, CancellationToken cancellationToken)
        {
            if (query == "older")
            {
                OlderSearchStarted.TrySetResult(true);
                return await OlderSearchCompletion.Task.WaitAsync(cancellationToken);
            }

            return query == "newer"
                ? [new DiscordGuildMember("500000000000000011", "Newer result", [])]
                : [new DiscordGuildMember(DiscordBrowserMemberId, "Member", [])];
        }

        public Task<DiscordGuildMember?> GetMemberAsync(string token, string guildId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<DiscordGuildMember?>(userId == DiscordBrowserMemberId
                ? new DiscordGuildMember(userId, "Member", [])
                : null);

        public Task<DiscordApiSendResult> SendMessageAsync(string token, string channelId, DiscordMessageRequest request, DiscordValidatedImage? image, CancellationToken cancellationToken)
        {
            Sent.Add(new SentDiscordMessage(
                request,
                image?.Bytes.ToArray(),
                image?.Spoiler ?? false,
                image?.EmbedPlacement ?? false));
            SendCompleted.TrySetResult(true);
            return Task.FromResult(new DiscordApiSendResult(DiscordDeliveryStatus.Success, "500000000000000099"));
        }

        internal sealed record SentDiscordMessage(
            DiscordMessageRequest Request,
            byte[]? ImageBytes,
            bool Spoiler,
            bool EmbedPlacement);
    }
}
