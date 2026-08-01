using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class SecurityLeakCanaryTests
{
    [Fact]
    public async Task SyntheticSecretsNeverEscapeRejectedBrowserAndAuthenticationPaths()
    {
        List<string> logs = [];
        await using CreatorToolkitWebFactory factory = new(
            services => services.AddLogging(
                logging => logging.AddProvider(new TestLoggerProvider(logs))));
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        LeakCanary password = LeakCanary.Create("password");
        LeakCanary bootstrap = LeakCanary.Create("bootstrap capability");
        LeakCanary activation = LeakCanary.Create("activation capability");
        LeakCanary recovery = LeakCanary.Create("recovery capability");
        LeakCanary authenticationCookie = LeakCanary.Create("authentication cookie");
        LeakCanary authorization = LeakCanary.Create("authorization header");
        LeakCanary protectedSecret = LeakCanary.Create("protected-secret plaintext");
        LeakCanary connectionString = LeakCanary.Create("connection-string-like value");
        LeakCanary dataPath = LeakCanary.Create("machine-specific data path");
        LeakCanary keyPath = LeakCanary.Create("Data Protection key path");
        LeakCanary[] canaries =
        [
            password,
            bootstrap,
            activation,
            recovery,
            authenticationCookie,
            authorization,
            protectedSecret,
            connectionString,
            dataPath,
            keyPath,
        ];

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ISecretStore>()
                .CreateAsync("security-regression", protectedSecret.Value);
        }

        List<HttpResponseMessage> responses = [];
        string loginHtml = await client.GetStringAsync("/Login");
        using (HttpRequestMessage login = new(HttpMethod.Post, "/Login"))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                authorization.Value);
            login.Headers.Add(
                "Cookie",
                $"creator-toolkit-auth={authenticationCookie.Value}");
            login.Headers.Add("X-Connection-Marker", connectionString.Value);
            login.Headers.Add("X-Data-Path-Marker", dataPath.Value);
            login.Headers.Add("X-Key-Path-Marker", keyPath.Value);
            login.Content = Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(loginHtml)),
                ("UserName", "unknown-local"),
                ("Password", password.Value));
            responses.Add(await client.SendAsync(login));
        }

        string setupHtml = await client.GetStringAsync("/Setup");
        responses.Add(
            await client.PostAsync(
                "/Setup",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(setupHtml)),
                    ("Capability", bootstrap.Value),
                    ("UserName", "canary-owner"),
                    ("Password", password.Value),
                    ("ConfirmPassword", password.Value))));

        string activationHtml = await client.GetStringAsync("/Account/Activate");
        responses.Add(
            await client.PostAsync(
                "/Account/Activate",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(activationHtml)),
                    ("Capability", activation.Value),
                    ("Password", password.Value),
                    ("ConfirmPassword", password.Value))));

        string recoveryHtml = await client.GetStringAsync("/Account/RecoverOwner");
        responses.Add(
            await client.PostAsync(
                "/Account/RecoverOwner",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(recoveryHtml)),
                    ("Capability", recovery.Value),
                    ("NewPassword", password.Value),
                    ("ConfirmPassword", password.Value))));

        foreach (HttpResponseMessage response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Found,
                "The exercised security path returned an unexpected status.");
            string body = await response.Content.ReadAsStringAsync();
            foreach (LeakCanary canary in canaries)
            {
                canary.AssertAbsent("HTTP response body", body);
                canary.AssertAbsent(response);
            }
        }

        foreach (LeakCanary canary in canaries)
        {
            canary.AssertAbsent("captured application logs", logs);
        }

        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        var auditRecords = await db.AuditRecords
            .AsNoTracking()
            .Select(
                record => new
                {
                    record.EventCode,
                    record.Outcome,
                    record.ReasonCode,
                    record.DiagnosticReference,
                })
            .ToArrayAsync();
        string auditData = string.Join(
            '|',
            auditRecords.Select(
                record => string.Join(
                    ':',
                    record.EventCode,
                    record.Outcome,
                    record.ReasonCode,
                    record.DiagnosticReference)));
        var diagnosticRecords = await db.DiagnosticRecords
            .AsNoTracking()
            .Select(
                record => new
                {
                    record.Reference,
                    record.Category,
                    record.ErrorCode,
                    record.Operation,
                    record.ExceptionType,
                })
            .ToArrayAsync();
        string diagnosticData = string.Join(
            '|',
            diagnosticRecords.Select(
                record => string.Join(
                    ':',
                    record.Reference,
                    record.Category,
                    record.ErrorCode,
                    record.Operation,
                    record.ExceptionType)));
        string[] ciphertext = await db.ProtectedSecrets
            .AsNoTracking()
            .Select(record => record.Ciphertext)
            .ToArrayAsync();
        Assert.Single(ciphertext);
        foreach (LeakCanary canary in canaries)
        {
            canary.AssertAbsent("audit records", auditData);
            canary.AssertAbsent("diagnostic records", diagnosticData);
            canary.AssertAbsent("protected-secret ciphertext", ciphertext);
        }

        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.login-rejected"));

        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public void CapabilityScriptsScrubFragmentsWithoutBrowserStorageOrCookies()
    {
        DirectoryInfo repository = FindRepositoryRoot();
        string[] scripts =
        [
            "src/TemperedTyrant.CreatorToolkit.Web/wwwroot/js/setup.js",
            "src/TemperedTyrant.CreatorToolkit.Web/wwwroot/js/account-activate.js",
            "src/TemperedTyrant.CreatorToolkit.Web/wwwroot/js/owner-recovery.js",
        ];

        foreach (string relativePath in scripts)
        {
            string source = File.ReadAllText(Path.Combine(repository.FullName, relativePath));
            Assert.Contains("history.replaceState", source, StringComparison.Ordinal);
            Assert.Contains("location.hash", source, StringComparison.Ordinal);
            Assert.DoesNotContain("localStorage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("sessionStorage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("document.cookie", source, StringComparison.Ordinal);
        }
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.ToDictionary(field => field.Key, field => field.Value));

    private static string GetAntiforgeryToken(string html)
    {
        Match match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TemperedTyrant.CreatorToolkit.slnx")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();
}
