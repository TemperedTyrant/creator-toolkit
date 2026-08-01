using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class ApplicationShellHttpTests
{
    private const string Password = "mild river orbit velvet canyon";
    private const string ChangedPassword = "silver meadow lantern compass";

    private static readonly string[] ProductRoutes =
    [
        "/Dashboard",
        "/Announcements",
        "/Destinations",
        "/Event-Sources",
        "/Publish-History",
    ];

    private static readonly string[] AdministrationRoutes =
    [
        "/Users",
        "/Settings",
        "/Debug",
    ];

    private static readonly string[] PersonalSecurityRoutes =
    [
        "/ChangePassword",
        "/Logout",
    ];

    [Fact]
    public async Task RoutesEnforceCompleteRoleAndDisabledAccountMatrix()
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        CreatedAccount admin = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "shell-admin",
            SystemRoles.Admin);
        CreatedAccount editor = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "shell-editor",
            SystemRoles.Editor);
        CreatedAccount viewer = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "shell-viewer",
            SystemRoles.Viewer);
        CreatedAccount disabled = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "shell-disabled",
            SystemRoles.Viewer);

        using HttpClient ownerClient = CreateClient(factory);
        using HttpClient adminClient = CreateClient(factory);
        using HttpClient editorClient = CreateClient(factory);
        using HttpClient viewerClient = CreateClient(factory);
        using HttpClient disabledClient = CreateClient(factory);
        using HttpClient anonymousClient = CreateClient(factory);
        await LoginAsync(ownerClient, "owner-local", Password);
        await LoginAsync(adminClient, admin.UserName, Password);
        await LoginAsync(editorClient, editor.UserName, Password);
        await LoginAsync(viewerClient, viewer.UserName, Password);
        await LoginAsync(disabledClient, disabled.UserName, Password);
        await DisableAsync(factory.Services, ownerId, disabled.Id);

        foreach (string route in ProductRoutes
            .Concat(PersonalSecurityRoutes)
            .Concat(AdministrationRoutes))
        {
            HttpResponseMessage anonymous = await anonymousClient.GetAsync(route);
            Assert.Equal(HttpStatusCode.Found, anonymous.StatusCode);
            Assert.Equal("/Login", anonymous.Headers.Location?.AbsolutePath);
            Assert.Contains(
                $"ReturnUrl={Uri.EscapeDataString(route)}",
                anonymous.Headers.Location?.Query,
                StringComparison.Ordinal);
        }

        foreach (string route in ProductRoutes.Concat(PersonalSecurityRoutes))
        {
            Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await editorClient.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewerClient.GetAsync(route)).StatusCode);
        }

        foreach (string route in AdministrationRoutes)
        {
            Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(route)).StatusCode);
            AssertAccessDenied(await editorClient.GetAsync(route));
            AssertAccessDenied(await viewerClient.GetAsync(route));
        }

        foreach (string route in ProductRoutes
            .Concat(PersonalSecurityRoutes)
            .Concat(AdministrationRoutes))
        {
            HttpResponseMessage disabledResponse = await disabledClient.GetAsync(route);
            Assert.Equal(HttpStatusCode.Found, disabledResponse.StatusCode);
            Assert.Equal("/Login", disabledResponse.Headers.Location?.AbsolutePath);
        }
    }

    [Fact]
    public async Task NavigationAndUsersActionsMatchServerSideAuthorization()
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        CreatedAccount admin = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "directory-admin",
            SystemRoles.Admin);
        CreatedAccount editor = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "directory-editor",
            SystemRoles.Editor);

        using HttpClient ownerClient = CreateClient(factory);
        using HttpClient adminClient = CreateClient(factory);
        using HttpClient editorClient = CreateClient(factory);
        await LoginAsync(ownerClient, "owner-local", Password);
        await LoginAsync(adminClient, admin.UserName, Password);
        await LoginAsync(editorClient, editor.UserName, Password);

        string ownerHtml = await ownerClient.GetStringAsync("/Users");
        Assert.Contains("href=\"/Users\"", ownerHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/Settings\"", ownerHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/Debug\"", ownerHtml, StringComparison.Ordinal);
        Assert.Contains(
            $"/Account/ManageUser?userId={admin.Id}",
            ownerHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transfer ownership", ownerHtml, StringComparison.Ordinal);

        string adminHtml = await adminClient.GetStringAsync("/Users");
        Assert.DoesNotContain(
            $"/Account/ManageUser?userId={ownerId}",
            adminHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"/Account/ManageUser?userId={admin.Id}",
            adminHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"/Account/ManageUser?userId={editor.Id}",
            adminHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer ownership", adminHtml, StringComparison.Ordinal);

        string editorHtml = await editorClient.GetStringAsync("/Dashboard");
        Assert.DoesNotContain("href=\"/Users\"", editorHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Settings\"", editorHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Debug\"", editorHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedMutationsAreDeniedAndAntiforgeryIsRequired()
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        CreatedAccount editor = await CreateActiveAsync(
            factory.Services,
            ownerId,
            "forgery-editor",
            SystemRoles.Editor);
        using HttpClient ownerClient = CreateClient(factory);
        using HttpClient editorClient = CreateClient(factory);
        await LoginAsync(ownerClient, "owner-local", Password);
        await LoginAsync(editorClient, editor.UserName, Password);

        string[] protectedMutationPaths =
        [
            "/Account/CreateUser",
            "/Account/ManageUser?handler=Role",
            "/Account/ManageUser?handler=Disable",
            "/Account/ManageUser?handler=Delete",
            "/Account/ManageUser?handler=RegenerateActivation",
            "/Account/TransferOwnership",
            "/ChangePassword",
            "/Logout",
        ];
        foreach (string path in protectedMutationPaths)
        {
            HttpResponseMessage missingAntiforgery = await ownerClient.PostAsync(
                path,
                Form(("UserName", "forged-user"), ("Role", SystemRoles.Viewer)));
            Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        }

        string editorDashboard = await editorClient.GetStringAsync("/Dashboard");
        HttpResponseMessage unauthorizedMutation = await editorClient.PostAsync(
            "/Account/CreateUser",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(editorDashboard)),
                ("UserName", "forged-user"),
                ("Role", SystemRoles.Viewer)));
        AssertAccessDenied(unauthorizedMutation);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.False(
            await scope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .Users
                .AnyAsync(user => user.UserName == "forged-user"));
    }

    [Fact]
    public async Task LoginAndPasswordChangeHonorOnlyLocalReturnUrls()
    {
        await using CreatorToolkitWebFactory factory = new();
        await InitializeOwnerAsync(factory.Services);
        using HttpClient localClient = CreateClient(factory);

        HttpResponseMessage requestedPageLogin = await LoginAsync(
            localClient,
            "owner-local",
            Password,
            "/Announcements");
        Assert.Equal("/Announcements", requestedPageLogin.Headers.Location?.OriginalString);

        string changeHtml = await localClient.GetStringAsync(
            "/ChangePassword?ReturnUrl=%2FSettings");
        HttpResponseMessage changed = await localClient.PostAsync(
            "/ChangePassword",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(changeHtml)),
                ("ReturnUrl", "/Settings"),
                ("CurrentPassword", Password),
                ("NewPassword", ChangedPassword),
                ("ConfirmPassword", ChangedPassword)));
        Assert.Equal(HttpStatusCode.Found, changed.StatusCode);
        Assert.Equal("/Settings", changed.Headers.Location?.OriginalString);

        using HttpClient externalClient = CreateClient(factory);
        HttpResponseMessage externalLogin = await LoginAsync(
            externalClient,
            "owner-local",
            ChangedPassword,
            "https://attacker.invalid/collect");
        Assert.Equal("/Dashboard", externalLogin.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task RenderedShellIsHonestResponsiveCspCompatibleAndSecretFree()
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        const string reference = "CTK-0123456789ABCDEF0123456789ABCDEF";
        string securityStamp;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = scope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            securityStamp = await db.Users
                .Where(user => user.Id == ownerId)
                .Select(user => user.SecurityStamp!)
                .SingleAsync();
            db.DiagnosticRecords.Add(
                DiagnosticRecord.Create(
                    new DiagnosticReference(reference),
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.Infrastructure,
                        DiagnosticOperation.PersistenceInitialization,
                        DiagnosticExceptionType.Database),
                    new DateTimeOffset(2044, 5, 6, 7, 8, 9, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        }
        await using (AsyncServiceScope statusScope = factory.Services.CreateAsyncScope())
        {
            var status = await statusScope.ServiceProvider
                .GetRequiredService<
                    TemperedTyrant.CreatorToolkit.Web.Diagnostics.DebugStatusService>()
                .GetAsync();
            Assert.True(status.DatabaseAccessible);
            Assert.True(status.MigrationsCurrent);
            Assert.Contains(
                status.RecentDiagnostics,
                diagnostic => diagnostic.Reference == reference);
        }

        using HttpClient client = CreateClient(factory);
        await LoginAsync(client, "owner-local", Password);
        string dashboard = await client.GetStringAsync("/Dashboard");
        Assert.Contains("owner-local", dashboard, StringComparison.Ordinal);
        Assert.Contains(">Owner<", dashboard, StringComparison.Ordinal);
        Assert.Contains("Draft authoring available", dashboard, StringComparison.Ordinal);
        Assert.Contains("Not implemented", dashboard, StringComparison.Ordinal);
        Assert.Contains(
            "name=\"viewport\"",
            dashboard,
            StringComparison.Ordinal);
        Assert.Contains("href=\"/css/site.css\"", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", dashboard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", dashboard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", dashboard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", dashboard, StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage announcements = await client.GetAsync("/Announcements");
        string announcementsHtml = await announcements.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, announcements.StatusCode);
        Assert.Contains("Create announcement", announcementsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Not implemented", announcementsHtml, StringComparison.Ordinal);

        HttpResponseMessage destinations = await client.GetAsync("/Destinations");
        string destinationsHtml = await destinations.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, destinations.StatusCode);
        Assert.Contains("Add Discord bot", destinationsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Not implemented", destinationsHtml, StringComparison.Ordinal);

        foreach (string route in ProductRoutes.Skip(3))
        {
            HttpResponseMessage response = await client.GetAsync(route);
            string html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Not implemented", html, StringComparison.Ordinal);
            int firstForm = html.IndexOf("<form", StringComparison.OrdinalIgnoreCase);
            Assert.True(firstForm >= 0);
            Assert.Equal(
                firstForm,
                html.LastIndexOf("<form", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(">Create<", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">Connect<", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">Publish<", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">Schedule<", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">Retry<", html, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "default-src 'self'; object-src 'none'; base-uri 'none'; "
                + "frame-ancestors 'none'; form-action 'self'",
                Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        }

        string debug = await client.GetStringAsync("/Debug");
        Assert.Contains("<dd>Running</dd>", debug, StringComparison.Ordinal);
        Assert.Contains(reference, debug, StringComparison.Ordinal);
        Assert.Contains("infrastructure-failure", debug, StringComparison.Ordinal);
        Assert.DoesNotContain(securityStamp, debug, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, debug, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.DataDirectory, debug, StringComparison.Ordinal);
        Assert.DoesNotContain("ExceptionType", debug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", debug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", debug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".db", debug, StringComparison.OrdinalIgnoreCase);

        string css = await client.GetStringAsync("/css/site.css");
        Assert.Contains("@media", css, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid> InitializeOwnerAsync(IServiceProvider services)
    {
        string raw = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("application-shell-owner")));
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        InitialOwnerSetupResult result = await scope.ServiceProvider
            .GetRequiredService<InitialOwnerSetupService>()
            .CreateAsync(
                new InitialOwnerSetupRequest(
                    raw,
                    "owner-local",
                    "Owner",
                    Password));
        Assert.Equal(InitialOwnerSetupStatus.Succeeded, result.Status);
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Select(user => user.Id)
            .SingleAsync();
    }

    private static async Task<CreatedAccount> CreateActiveAsync(
        IServiceProvider services,
        Guid ownerId,
        string userName,
        string role)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserLifecycleResult pending = await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .CreatePendingAsync(ownerId, userName, null, role);
        Assert.Equal(UserLifecycleStatus.Succeeded, pending.Status);
        AccountActivationResult activation = await scope.ServiceProvider
            .GetRequiredService<AccountActivationService>()
            .ActivateAsync(pending.OneTimeActivationCapability!, Password);
        Assert.Equal(AccountActivationStatus.Succeeded, activation.Status);
        return new CreatedAccount(pending.TargetUserId!.Value, userName);
    }

    private static async Task DisableAsync(
        IServiceProvider services,
        Guid ownerId,
        Guid userId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        string stamp = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.ConcurrencyStamp!)
            .SingleAsync();
        Assert.Equal(
            UserLifecycleStatus.Succeeded,
            (await scope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .DisableAsync(ownerId, userId, stamp)).Status);
    }

    private static HttpClient CreateClient(CreatorToolkitWebFactory factory) =>
        factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string userName,
        string password,
        string? returnUrl = null)
    {
        string path = returnUrl is null
            ? "/Login"
            : $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        string html = await client.GetStringAsync(path);
        HttpResponseMessage response = await client.PostAsync(
            "/Login",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(html)),
                ("ReturnUrl", returnUrl ?? string.Empty),
                ("UserName", userName),
                ("Password", password)));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return response;
    }

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Name, value.Value)));

    private static string GetAntiforgeryToken(string html)
    {
        Match match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private static void AssertAccessDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/AccessDenied", response.Headers.Location?.AbsolutePath);
    }

    private sealed record CreatedAccount(Guid Id, string UserName);

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();
}
