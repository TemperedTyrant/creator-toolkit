using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Authorization;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AuthenticationAndSetupHttpTests
{
    private const string ValidPassword = "mild river orbit velvet canyon";

    [Fact]
    public async Task SetupFragmentIsNotSentOrRenderedAndFailureDoesNotEchoCapability()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        const string rawCapability = "browser-fragment-capability-marker";

        HttpResponseMessage getResponse = await client.GetAsync(
            $"/Setup#token={rawCapability}");
        string getHtml = await getResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.DoesNotContain(rawCapability, getHtml, StringComparison.Ordinal);
        Assert.Contains(
            "<form method=\"post\" id=\"setup-form\"",
            getHtml,
            StringComparison.Ordinal);
        Assert.Matches(CapabilityPasswordInputPattern(), getHtml);
        Assert.DoesNotMatch(CapabilityHiddenInputPattern(), getHtml);
        Assert.DoesNotContain("http://", getHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", getHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "no-store",
            getResponse.Headers.CacheControl?.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "no-referrer",
            Assert.Single(getResponse.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(
            "default-src 'none'; script-src 'self'; object-src 'none'; "
            + "base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
            Assert.Single(getResponse.Headers.GetValues("Content-Security-Policy")));
        Assert.False(getResponse.Headers.Contains("Strict-Transport-Security"));

        string antiforgery = GetAntiforgeryToken(getHtml);
        HttpResponseMessage postResponse = await client.PostAsync(
            "/Setup",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = antiforgery,
                    ["Capability"] = rawCapability,
                    ["UserName"] = "owner-local",
                    ["DisplayName"] = string.Empty,
                    ["Password"] = ValidPassword,
                    ["ConfirmPassword"] = ValidPassword,
                }));
        string postHtml = await postResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.Contains(
            "The bootstrap token is invalid or expired.",
            postHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(rawCapability, postHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidPassword, postHtml, StringComparison.Ordinal);

        string queryHtml = await client.GetStringAsync(
            $"/Setup?Capability={Uri.EscapeDataString(rawCapability)}");
        Assert.DoesNotContain(rawCapability, queryHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupScriptRemovesFragmentBeforeKeepingCapabilityInFormState()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string script = await client.GetStringAsync("/js/setup.js");

        int fragmentRead = script.IndexOf("window.location.hash", StringComparison.Ordinal);
        int fragmentRemoval = script.IndexOf("history.replaceState", StringComparison.Ordinal);
        int formAssignment = script.IndexOf("capabilityInput.value = capability", StringComparison.Ordinal);
        Assert.True(fragmentRead >= 0);
        Assert.True(fragmentRemoval > fragmentRead);
        Assert.True(formAssignment > fragmentRemoval);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.cookie", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginFailureIsGenericRateLimitedAndDoesNotCreateDiagnostics()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        string loginHtml = await client.GetStringAsync("/Login");
        string antiforgery = GetAntiforgeryToken(loginHtml);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpRequestMessage request = CreateFormPost(
                "/Login",
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = antiforgery,
                    ["UserName"] = attempt == 0
                        ? "missing-local-user"
                        : "someone@example.invalid",
                    ["Password"] = "incorrect-password-value",
                });
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{attempt + 1}");
            HttpResponseMessage post = await client.SendAsync(request);
            string responseHtml = await post.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            Assert.Contains(
                "The username or password is incorrect.",
                responseHtml,
                StringComparison.Ordinal);
        }

        using HttpRequestMessage limitedRequest = CreateFormPost(
            "/Login",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgery,
                ["UserName"] = "missing-local-user",
                ["Password"] = "incorrect-password-value",
            });
        limitedRequest.Headers.Add("X-Forwarded-For", "192.0.2.250");
        HttpResponseMessage limited = await client.SendAsync(limitedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Login")).StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());
    }

    [Fact]
    public async Task SetupAndLoginUseSeparatePostOnlyRateLimits()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        string setupHtml = await client.GetStringAsync("/Setup");
        string setupAntiforgery = GetAntiforgeryToken(setupHtml);
        string invalidCapability = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("invalid-setup-rate-token")));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await client.PostAsync(
                "/Setup",
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["__RequestVerificationToken"] = setupAntiforgery,
                        ["Capability"] = invalidCapability,
                        ["UserName"] = "owner-local",
                        ["Password"] = ValidPassword,
                        ["ConfirmPassword"] = ValidPassword,
                    }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        HttpResponseMessage setupLimited = await client.PostAsync(
            "/Setup",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = setupAntiforgery,
                    ["Capability"] = invalidCapability,
                    ["UserName"] = "owner-local",
                    ["Password"] = ValidPassword,
                    ["ConfirmPassword"] = ValidPassword,
                }));
        Assert.Equal(HttpStatusCode.TooManyRequests, setupLimited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Setup")).StatusCode);

        string loginHtml = await client.GetStringAsync("/Login");
        HttpResponseMessage loginFailure = await client.PostAsync(
            "/Login",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(loginHtml),
                    ["UserName"] = "missing-local-user",
                    ["Password"] = "incorrect-password-value",
                }));
        Assert.Equal(HttpStatusCode.OK, loginFailure.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());
    }

    [Fact]
    public async Task RequiredSetupAuditFailureRollsBackAndCannotExposeSubmittedSecrets()
    {
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddScoped<IAuditWriter, FailingInitialOwnerAuditWriter>();
            });
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        string rawCapability = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("audit-failure-capability")));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>()
                .IssueAsync(SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability)));
        }

        string setupHtml = await client.GetStringAsync("/Setup");
        HttpResponseMessage response = await client.PostAsync(
            "/Setup",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(setupHtml),
                    ["Capability"] = rawCapability,
                    ["UserName"] = "owner-local",
                    ["Password"] = ValidPassword,
                    ["ConfirmPassword"] = ValidPassword,
                }));
        string responseHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Diagnostic reference", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(rawCapability, responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidPassword, responseHtml, StringComparison.Ordinal);

        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.UserRoles.CountAsync());
        Assert.Equal(0, await db.Workspaces.CountAsync());
        Assert.Equal(0, await db.Ownerships.CountAsync());
        Assert.Null((await db.InstallationStates.SingleAsync()).InitializedAtUtc);
        Assert.Equal(1, await db.DiagnosticRecords.CountAsync());
        Assert.False(DatabaseFilesContain(factory.DataDirectory, rawCapability));
        Assert.False(DatabaseFilesContain(factory.DataDirectory, ValidPassword));
    }

    [Fact]
    public async Task AuthenticationCookieUsesRequiredFlagsAndHonorsEffectiveScheme()
    {
        await AssertCookieFlagsAsync(new Uri("http://127.0.0.1"), expectSecure: false);
        await AssertCookieFlagsAsync(new Uri("https://creator.test"), expectSecure: true);
    }

    [Fact]
    public async Task SecurityStampIsValidatedOnEveryAuthenticatedRequest()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await SetupOwnerThroughHttpAsync(factory, client);
        await LoginThroughHttpAsync(client);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/ChangePassword")).StatusCode);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = Assert.Single(await users.Users.ToArrayAsync());
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        HttpResponseMessage afterStampChange = await client.GetAsync("/ChangePassword");
        Assert.Equal(HttpStatusCode.Redirect, afterStampChange.StatusCode);
        Assert.Equal("/Login", afterStampChange.Headers.Location?.AbsolutePath);

        TimeSpan interval = factory.Services
            .GetRequiredService<IOptions<SecurityStampValidatorOptions>>()
            .Value
            .ValidationInterval;
        Assert.Equal(TimeSpan.Zero, interval);
    }

    [Fact]
    public async Task DisabledAccountIsRejectedOnTheNextRequestWithoutAStampChange()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await SetupOwnerThroughHttpAsync(factory, client);
        await LoginThroughHttpAsync(client, "OWNER-LOCAL");

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = await users.Users.SingleAsync();
            string securityStamp = user.SecurityStamp!;
            user.IsEnabled = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            Assert.Equal(securityStamp, user.SecurityStamp);
        }

        HttpResponseMessage response = await client.GetAsync("/ChangePassword");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value =>
                value.StartsWith("creator-toolkit-auth=", StringComparison.Ordinal)
                && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecurityStampValidationInfrastructureFailureFailsClosed()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await SetupOwnerThroughHttpAsync(factory, client);
        await LoginThroughHttpAsync(client);
        SqliteConnection.ClearAllPools();
        string databasePath = Path.Combine(
            factory.DataDirectory,
            "creator-toolkit.db");
        File.Move(databasePath, $"{databasePath}.offline");

        HttpResponseMessage response = await client.GetAsync("/ChangePassword");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("<h1>Change password</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePasswordAndLogoutUseIdentityAndTransactionalAudits()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await SetupOwnerThroughHttpAsync(factory, client);
        await LoginThroughHttpAsync(client);
        const string newPassword = "silver meadow lantern compass";

        string changeHtml = await client.GetStringAsync("/ChangePassword");
        HttpResponseMessage change = await client.PostAsync(
            "/ChangePassword",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(changeHtml),
                    ["CurrentPassword"] = ValidPassword,
                    ["NewPassword"] = newPassword,
                    ["ConfirmPassword"] = newPassword,
                }));
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        string logoutHtml = await client.GetStringAsync("/Logout");
        HttpResponseMessage logout = await client.PostAsync(
            "/Logout",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(logoutHtml),
                }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/Login", logout.Headers.Location?.OriginalString);
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.GetAsync("/ChangePassword")).StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        ApplicationUser user = await users.Users.SingleAsync();
        Assert.False(await users.CheckPasswordAsync(user, ValidPassword));
        Assert.True(await users.CheckPasswordAsync(user, newPassword));
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.password-changed"));
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.logout-succeeded"));
    }

    [Fact]
    public async Task IdentityLockoutConfigurationMatchesTheFixedPolicy()
    {
        await using CreatorToolkitWebFactory factory = new();
        IdentityOptions options = factory.Services
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;

        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
        Assert.False(options.Password.RequireUppercase);
        Assert.False(options.Password.RequireLowercase);
        Assert.False(options.Password.RequireDigit);
        Assert.False(options.Password.RequireNonAlphanumeric);
        Assert.Equal(15, options.Password.RequiredLength);
        Assert.Equal(1, options.Password.RequiredUniqueChars);
        Assert.False(options.User.RequireUniqueEmail);
        Assert.False(options.SignIn.RequireConfirmedEmail);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAccountWithoutChangingTheSafeResponse()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await SetupOwnerThroughHttpAsync(factory, client);
        string loginHtml = await client.GetStringAsync("/Login");
        string antiforgery = GetAntiforgeryToken(loginHtml);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await client.PostAsync(
                "/Login",
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["__RequestVerificationToken"] = antiforgery,
                        ["UserName"] = "owner-local",
                        ["Password"] = "wrong-password-value",
                    }));
            string responseHtml = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "The username or password is incorrect.",
                responseHtml,
                StringComparison.Ordinal);
        }

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        ApplicationUser user = await users.Users.SingleAsync();
        Assert.True(await users.IsLockedOutAsync(user));
        Assert.Equal(
            5,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.login-rejected"));
        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());
    }

    [Fact]
    public async Task CentralPoliciesEnforceTheStableRoleHierarchy()
    {
        await using CreatorToolkitWebFactory factory = new();
        IAuthorizationService authorization = factory.Services
            .GetRequiredService<IAuthorizationService>();
        var owner = PrincipalForRole(SystemRoles.Owner);
        var admin = PrincipalForRole(SystemRoles.Admin);
        var editor = PrincipalForRole(SystemRoles.Editor);
        var viewer = PrincipalForRole(SystemRoles.Viewer);

        Assert.True((await authorization.AuthorizeAsync(
            owner,
            null,
            AuthorizationPolicies.OwnerOnly)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(
            admin,
            null,
            AuthorizationPolicies.OwnerOnly)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            admin,
            null,
            AuthorizationPolicies.Administration)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(
            editor,
            null,
            AuthorizationPolicies.Administration)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            editor,
            null,
            AuthorizationPolicies.ContentEditing)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            viewer,
            null,
            AuthorizationPolicies.ApplicationAccess)).Succeeded);
    }

    private static async Task AssertCookieFlagsAsync(
        Uri baseAddress,
        bool expectSecure)
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = baseAddress,
                HandleCookies = true,
            });
        if (!expectSecure)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        }
        await SetupOwnerThroughHttpAsync(factory, client);

        HttpResponseMessage response = await LoginThroughHttpAsync(client);
        string authCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                    "creator-toolkit-auth=",
                    StringComparison.Ordinal));

        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            expectSecure,
            authCookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SetupOwnerThroughHttpAsync(
        CreatorToolkitWebFactory factory,
        HttpClient client)
    {
        string rawCapability = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("http-setup-capability")));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>()
                .IssueAsync(SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability)));
        }

        string setupHtml = await client.GetStringAsync("/Setup");
        HttpResponseMessage setup = await client.PostAsync(
            "/Setup",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(setupHtml),
                    ["Capability"] = rawCapability,
                    ["UserName"] = "owner-local",
                    ["DisplayName"] = string.Empty,
                    ["Password"] = ValidPassword,
                    ["ConfirmPassword"] = ValidPassword,
                }));
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.Equal("/Login", setup.Headers.Location?.OriginalString);
    }

    private static async Task<HttpResponseMessage> LoginThroughHttpAsync(
        HttpClient client,
        string userName = "owner-local")
    {
        string loginHtml = await client.GetStringAsync("/Login");
        HttpResponseMessage response = await client.PostAsync(
            "/Login",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = GetAntiforgeryToken(loginHtml),
                    ["UserName"] = userName,
                    ["Password"] = ValidPassword,
                }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response;
    }

    private static HttpRequestMessage CreateFormPost(
        string path,
        IReadOnlyDictionary<string, string> fields)
    {
        return new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields),
        };
    }

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

    private sealed class FailingInitialOwnerAuditWriter(
        CreatorToolkitDbContext dbContext,
        TimeProvider timeProvider) : IAuditWriter
    {
        public Task WriteAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (auditEvent.EventCode == AuditEventCode.InitialOwnerCreated)
            {
                throw new InvalidOperationException("Synthetic required audit failure.");
            }

            dbContext.AuditRecords.Add(
                AuditRecord.Create(
                    auditEvent,
                    timeProvider.GetUtcNow().ToUniversalTime()));
            return Task.CompletedTask;
        }
    }

    private static System.Security.Claims.ClaimsPrincipal PrincipalForRole(string role)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier,
                    Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.Role,
                    role),
            ],
            IdentityConstants.ApplicationScheme);
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }

    private static string GetAntiforgeryToken(string html)
    {
        Match match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();

    [GeneratedRegex(
        """<input(?=[^>]*name="Capability")(?=[^>]*type="password")[^>]*>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPasswordInputPattern();

    [GeneratedRegex(
        """<input(?=[^>]*name="Capability")(?=[^>]*type="hidden")[^>]*>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityHiddenInputPattern();
}
