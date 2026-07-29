using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class UserLifecycleHttpTests
{
    private const string OwnerPassword = "mild river orbit velvet canyon";
    private const string UserPassword = "silver meadow lantern compass";

    [Fact]
    public async Task PendingAndDisabledAccountsReceiveGenericLoginFailure()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        Guid ownerId = await SetupAndLoginOwnerAsync(factory, ownerClient);
        UserLifecycleResult pending = await CreatePendingAsync(
            factory.Services,
            ownerId,
            "pending-local",
            SystemRoles.Editor);
        using HttpClient pendingClient = CreateClient(factory);

        string loginHtml = await pendingClient.GetStringAsync("/Login");
        HttpResponseMessage pendingLogin = await pendingClient.PostAsync(
            "/Login",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(loginHtml)),
                ("UserName", "pending-local"),
                ("Password", UserPassword)));
        string pendingResponse = await pendingLogin.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, pendingLogin.StatusCode);
        Assert.Contains(
            "The username or password is incorrect.",
            pendingResponse,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            pendingLogin.Headers,
            header => header.Key == "Set-Cookie"
                && header.Value.Any(
                    value => value.StartsWith(
                        "creator-toolkit-auth=",
                        StringComparison.Ordinal)));

        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await ActivateAsync(
                factory.Services,
                pending.OneTimeActivationCapability!,
                UserPassword)).Status);
        string stamp = await GetConcurrencyStampAsync(
            factory.Services,
            pending.TargetUserId!.Value);
        await using (AsyncServiceScope disableScope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await disableScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DisableAsync(
                        ownerId,
                        pending.TargetUserId.Value,
                        stamp)).Status);
        }

        loginHtml = await pendingClient.GetStringAsync("/Login");
        HttpResponseMessage disabledLogin = await pendingClient.PostAsync(
            "/Login",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(loginHtml)),
                ("UserName", "pending-local"),
                ("Password", UserPassword)));
        Assert.Contains(
            "The username or password is incorrect.",
            await disabledLogin.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlersEnforceOwnerAdminEditorViewerManagementMatrix()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        Guid ownerId = await SetupAndLoginOwnerAsync(factory, ownerClient);
        CreatedUser admin = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "admin-local",
            SystemRoles.Admin);
        await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "editor-local",
            SystemRoles.Editor);
        await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "viewer-local",
            SystemRoles.Viewer);

        Assert.Equal(
            HttpStatusCode.OK,
            (await ownerClient.GetAsync("/Account/CreateUser")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ownerClient.GetAsync("/Account/TransferOwnership")).StatusCode);

        using HttpClient adminClient = CreateClient(factory);
        await LoginAsync(adminClient, "admin-local", UserPassword);
        string adminCreateHtml = await adminClient.GetStringAsync("/Account/CreateUser");
        HttpResponseMessage adminAllowed = await adminClient.PostAsync(
            "/Account/CreateUser",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(adminCreateHtml)),
                ("UserName", "admin-created-editor"),
                ("Role", SystemRoles.Editor)));
        Assert.Equal(HttpStatusCode.OK, adminAllowed.StatusCode);

        adminCreateHtml = await adminClient.GetStringAsync("/Account/CreateUser");
        HttpResponseMessage adminForged = await adminClient.PostAsync(
            "/Account/CreateUser",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(adminCreateHtml)),
                ("UserName", "admin-forged-admin"),
                ("Role", SystemRoles.Admin)));
        AssertAccessDenied(adminForged);
        AssertAccessDenied(await adminClient.GetAsync("/Account/TransferOwnership"));

        using HttpClient editorClient = CreateClient(factory);
        await LoginAsync(editorClient, "editor-local", UserPassword);
        AssertAccessDenied(await editorClient.GetAsync("/Account/CreateUser"));

        using HttpClient viewerClient = CreateClient(factory);
        await LoginAsync(viewerClient, "viewer-local", UserPassword);
        AssertAccessDenied(await viewerClient.GetAsync("/Account/CreateUser"));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.UserName == "admin-forged-admin"));
        Assert.True(await db.Users.AnyAsync(user => user.UserName == "admin-created-editor"));
        Assert.NotEqual(Guid.Empty, admin.Id);
    }

    [Fact]
    public async Task ActivationLinkIsShownOnceAndCapabilityPageNeverBindsQueryValues()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        await SetupAndLoginOwnerAsync(factory, ownerClient);
        string createHtml = await ownerClient.GetStringAsync("/Account/CreateUser");
        HttpResponseMessage created = await ownerClient.PostAsync(
            "/Account/CreateUser",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(createHtml)),
                ("UserName", "one-time-link-user"),
                ("Role", SystemRoles.Viewer)));
        string createdHtml = await created.Content.ReadAsStringAsync();
        Match capabilityMatch = ActivationCapabilityPattern().Match(createdHtml);
        Assert.True(capabilityMatch.Success);
        string rawCapability = capabilityMatch.Groups[1].Value;
        Assert.Equal(43, rawCapability.Length);
        Assert.Contains("Copy this link now", createdHtml, StringComparison.Ordinal);

        string refreshedHtml = await ownerClient.GetStringAsync("/Account/CreateUser");
        Assert.DoesNotContain(rawCapability, refreshedHtml, StringComparison.Ordinal);
        Assert.False(DatabaseFilesContain(factory.DataDirectory, rawCapability));

        using HttpClient anonymousClient = CreateClient(factory);
        HttpResponseMessage get = await anonymousClient.GetAsync(
            $"/Account/Activate?token={rawCapability}");
        string getHtml = await get.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.DoesNotContain(rawCapability, getHtml, StringComparison.Ordinal);
        Assert.Matches(CapabilityPasswordInputPattern(), getHtml);
        Assert.DoesNotMatch(CapabilityHiddenInputPattern(), getHtml);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        Assert.Equal(
            "no-referrer",
            Assert.Single(get.Headers.GetValues("Referrer-Policy")));
        Assert.Contains(
            "script-src 'self'",
            Assert.Single(get.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);

        HttpResponseMessage activated = await anonymousClient.PostAsync(
            "/Account/Activate",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(getHtml)),
                ("Capability", rawCapability),
                ("Password", UserPassword),
                ("ConfirmPassword", UserPassword)));
        Assert.Equal(HttpStatusCode.Redirect, activated.StatusCode);
        Assert.Equal("/Login", activated.Headers.Location?.OriginalString);
        Assert.False(DatabaseFilesContain(factory.DataDirectory, rawCapability));
        Assert.False(DatabaseFilesContain(factory.DataDirectory, UserPassword));
    }

    [Fact]
    public async Task RecoveryPageIsPostOnlyGenericAndUsesCapabilitySecurityProfile()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = CreateClient(factory);
        await SetupAndLoginOwnerAsync(factory, client);
        string raw = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("http-owner-recovery")));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<OwnerRecoveryIssuer>()
                .IssueAsync(Hash(raw));
        }
        HttpResponseMessage invalidatedOwnerSession = await client.GetAsync("/ChangePassword");
        Assert.Equal(HttpStatusCode.Found, invalidatedOwnerSession.StatusCode);
        Assert.Equal("/Login", invalidatedOwnerSession.Headers.Location?.AbsolutePath);

        using HttpClient anonymousClient = CreateClient(factory);
        HttpResponseMessage get = await anonymousClient.GetAsync(
            $"/Account/RecoverOwner?token={raw}");
        string html = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(raw, html, StringComparison.Ordinal);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        Assert.Equal(
            "no-referrer",
            Assert.Single(get.Headers.GetValues("Referrer-Policy")));

        HttpResponseMessage invalid = await anonymousClient.PostAsync(
            "/Account/RecoverOwner",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(html)),
                ("Capability", WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))),
                ("NewPassword", UserPassword),
                ("ConfirmPassword", UserPassword)));
        string invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", invalidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, invalidHtml, StringComparison.Ordinal);

        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        Assert.Equal(
            0,
            await verificationScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .DiagnosticRecords
                .CountAsync());
    }

    [Fact]
    public async Task ActivationAndRecoveryUseSeparatePostRateLimitsWithoutDiagnostics()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = CreateClient(factory);
        string activationHtml = await client.GetStringAsync("/Account/Activate");
        string activationAntiforgery = GetAntiforgeryToken(activationHtml);
        string invalidCapability = WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(32));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await client.PostAsync(
                "/Account/Activate",
                Form(
                    ("__RequestVerificationToken", activationAntiforgery),
                    ("Capability", invalidCapability),
                    ("Password", UserPassword),
                    ("ConfirmPassword", UserPassword)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "invalid or expired",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsync(
                "/Account/Activate",
                Form(
                    ("__RequestVerificationToken", activationAntiforgery),
                    ("Capability", invalidCapability),
                    ("Password", UserPassword),
                    ("ConfirmPassword", UserPassword)))).StatusCode);

        string recoveryHtml = await client.GetStringAsync("/Account/RecoverOwner");
        string recoveryAntiforgery = GetAntiforgeryToken(recoveryHtml);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await client.PostAsync(
                "/Account/RecoverOwner",
                Form(
                    ("__RequestVerificationToken", recoveryAntiforgery),
                    ("Capability", invalidCapability),
                    ("NewPassword", UserPassword),
                    ("ConfirmPassword", UserPassword)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "invalid or expired",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsync(
                "/Account/RecoverOwner",
                Form(
                    ("__RequestVerificationToken", recoveryAntiforgery),
                    ("Capability", invalidCapability),
                    ("NewPassword", UserPassword),
                    ("ConfirmPassword", UserPassword)))).StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.Equal(
            0,
            await scope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .DiagnosticRecords
                .CountAsync());
    }

    [Fact]
    public async Task RoleChangeInvalidatesTheAffectedSessionOnItsNextRequest()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        Guid ownerId = await SetupAndLoginOwnerAsync(factory, ownerClient);
        CreatedUser target = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "role-session-user",
            SystemRoles.Viewer);
        using HttpClient targetClient = CreateClient(factory);
        await LoginAsync(targetClient, "role-session-user", UserPassword);
        Assert.Equal(
            HttpStatusCode.OK,
            (await targetClient.GetAsync("/ChangePassword")).StatusCode);
        string concurrencyStamp = await GetConcurrencyStampAsync(
            factory.Services,
            target.Id);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await scope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .ChangeRoleAsync(
                        ownerId,
                        target.Id,
                        concurrencyStamp,
                        SystemRoles.Editor)).Status);
        }

        HttpResponseMessage nextRequest = await targetClient.GetAsync("/ChangePassword");
        Assert.Equal(HttpStatusCode.Found, nextRequest.StatusCode);
        Assert.Equal("/Login", nextRequest.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task AccountDeletionRejectsTheDeletedUsersSessionOnItsNextRequest()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        Guid ownerId = await SetupAndLoginOwnerAsync(factory, ownerClient);
        CreatedUser target = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "delete-session-user",
            SystemRoles.Viewer);
        using HttpClient targetClient = CreateClient(factory);
        await LoginAsync(targetClient, "delete-session-user", UserPassword);
        string concurrencyStamp = await GetConcurrencyStampAsync(
            factory.Services,
            target.Id);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await scope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DeleteAsync(
                        ownerId,
                        target.Id,
                        concurrencyStamp)).Status);
        }

        HttpResponseMessage nextRequest = await targetClient.GetAsync("/ChangePassword");
        Assert.Equal(HttpStatusCode.Found, nextRequest.StatusCode);
        Assert.Equal("/Login", nextRequest.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task OwnershipReauthenticationCountsFailuresDespiteForwardingSpoofing()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        Guid ownerId = await SetupAndLoginOwnerAsync(factory, ownerClient);
        CreatedUser target = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "transfer-lockout-target",
            SystemRoles.Editor);
        string transferHtml = await ownerClient.GetStringAsync(
            "/Account/TransferOwnership");
        string antiforgery = GetAntiforgeryToken(transferHtml);
        string revision = OwnershipRevisionPattern()
            .Match(transferHtml)
            .Groups[1]
            .Value;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "/Account/TransferOwnership")
            {
                Content = Form(
                    ("__RequestVerificationToken", antiforgery),
                    ("TargetUserId", target.Id.ToString()),
                    ("ExpectedOwnershipRevision", revision),
                    ("CurrentPassword", "incorrect-transfer-password")),
            };
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{attempt + 1}");
            HttpResponseMessage response = await ownerClient.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "Ownership transfer could not be verified.",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "incorrect-transfer-password",
                html,
                StringComparison.Ordinal);
        }

        await using (AsyncServiceScope lockedScope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = lockedScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser owner = await users.FindByIdAsync(ownerId.ToString())
                ?? throw new InvalidOperationException();
            Assert.True(await users.IsLockedOutAsync(owner));
        }

        HttpResponseMessage stillLocked = await ownerClient.PostAsync(
            "/Account/TransferOwnership",
            Form(
                ("__RequestVerificationToken", antiforgery),
                ("TargetUserId", target.Id.ToString()),
                ("ExpectedOwnershipRevision", revision),
                ("CurrentPassword", OwnerPassword)));
        Assert.Contains(
            "Ownership transfer could not be verified.",
            await stillLocked.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using (AsyncServiceScope expiryScope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = expiryScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser owner = await users.FindByIdAsync(ownerId.ToString())
                ?? throw new InvalidOperationException();
            Assert.True(
                (await users.SetLockoutEndDateAsync(
                    owner,
                    DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        }

        HttpResponseMessage recovered = await ownerClient.PostAsync(
            "/Account/TransferOwnership",
            Form(
                ("__RequestVerificationToken", antiforgery),
                ("TargetUserId", target.Id.ToString()),
                ("ExpectedOwnershipRevision", revision),
                ("CurrentPassword", OwnerPassword)));
        Assert.Equal(HttpStatusCode.Found, recovered.StatusCode);
        Assert.Equal("/Login", recovered.Headers.Location?.OriginalString);

        await using AsyncServiceScope verificationScope =
            factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(target.Id, (await db.Ownerships.SingleAsync()).OwnerUserId);
        Assert.Equal(
            6,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.ownership-transfer-rejected"));
    }

    private static async Task<Guid> SetupAndLoginOwnerAsync(
        CreatorToolkitWebFactory factory,
        HttpClient client)
    {
        string raw = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("lifecycle-http-bootstrap")));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>()
                .IssueAsync(Hash(raw));
        }

        string setupHtml = await client.GetStringAsync("/Setup");
        HttpResponseMessage setup = await client.PostAsync(
            "/Setup",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(setupHtml)),
                ("Capability", raw),
                ("UserName", "owner-local"),
                ("Password", OwnerPassword),
                ("ConfirmPassword", OwnerPassword)));
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        await LoginAsync(client, "owner-local", OwnerPassword);

        await using AsyncServiceScope idScope = factory.Services.CreateAsyncScope();
        return await idScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Select(user => user.Id)
            .SingleAsync();
    }

    private static async Task LoginAsync(
        HttpClient client,
        string userName,
        string password)
    {
        string loginHtml = await client.GetStringAsync("/Login");
        HttpResponseMessage login = await client.PostAsync(
            "/Login",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(loginHtml)),
                ("UserName", userName),
                ("Password", password)));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static async Task<UserLifecycleResult> CreatePendingAsync(
        IServiceProvider services,
        Guid actorId,
        string userName,
        string role)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserLifecycleResult result = await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .CreatePendingAsync(actorId, userName, null, role);
        Assert.Equal(UserLifecycleStatus.Succeeded, result.Status);
        return result;
    }

    private static async Task<CreatedUser> CreateAndActivateAsync(
        IServiceProvider services,
        Guid actorId,
        string userName,
        string role)
    {
        UserLifecycleResult pending = await CreatePendingAsync(
            services,
            actorId,
            userName,
            role);
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await ActivateAsync(
                services,
                pending.OneTimeActivationCapability!,
                UserPassword)).Status);
        return new(pending.TargetUserId!.Value);
    }

    private static async Task<AccountActivationResult> ActivateAsync(
        IServiceProvider services,
        string capability,
        string password)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<AccountActivationService>()
            .ActivateAsync(capability, password);
    }

    private static async Task<string> GetConcurrencyStampAsync(
        IServiceProvider services,
        Guid userId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Where(user => user.Id == userId)
            .Select(user => user.ConcurrencyStamp!)
            .SingleAsync();
    }

    private static HttpClient CreateClient(CreatorToolkitWebFactory factory)
    {
        return factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
    }

    private static FormUrlEncodedContent Form(
        params (string Name, string Value)[] values)
    {
        return new FormUrlEncodedContent(
            values.Select(value => new KeyValuePair<string, string>(value.Name, value.Value)));
    }

    private static void AssertAccessDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/AccessDenied", response.Headers.Location?.AbsolutePath);
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool DatabaseFilesContain(string dataDirectory, string value)
    {
        return Directory
            .EnumerateFiles(
                dataDirectory,
                "creator-toolkit.db*",
                SearchOption.TopDirectoryOnly)
            .Any(
                path => Encoding.Latin1
                    .GetString(File.ReadAllBytes(path))
                    .Contains(value, StringComparison.Ordinal));
    }

    private static string GetAntiforgeryToken(string html)
    {
        Match match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private sealed record CreatedUser(Guid Id);

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();

    [GeneratedRegex(
        """#token=([A-Za-z0-9_-]{43})""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ActivationCapabilityPattern();

    [GeneratedRegex(
        """<input(?=[^>]*name="Capability")(?=[^>]*type="password")[^>]*>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPasswordInputPattern();

    [GeneratedRegex(
        """<input(?=[^>]*name="Capability")(?=[^>]*type="hidden")[^>]*>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityHiddenInputPattern();

    [GeneratedRegex(
        """<input(?=[^>]*name="ExpectedOwnershipRevision")(?=[^>]*value="([^"]*)")[^>]*>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex OwnershipRevisionPattern();
}
