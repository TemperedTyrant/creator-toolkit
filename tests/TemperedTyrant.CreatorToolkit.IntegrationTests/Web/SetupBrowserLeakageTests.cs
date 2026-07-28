using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class SetupBrowserLeakageTests
{
    private const string ValidPassword = "mild river orbit velvet canyon";

    [Fact]
    public async Task ActivationFragmentIsScrubbedAndSentOnlyInPostBody()
    {
        using TestDataDirectory data = new();
        string rawCapability;
        await using (ServiceProvider provider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(provider);
            Guid ownerId = await InitializeOwnerAsync(provider, "activation-browser-bootstrap");
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            UserLifecycleResult pending = await scope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .CreatePendingAsync(
                    ownerId,
                    "browser-activation-user",
                    null,
                    SystemRoles.Editor);
            Assert.Equal(UserLifecycleStatus.Succeeded, pending.Status);
            rawCapability = pending.OneTimeActivationCapability!;
        }

        await AssertCapabilityBrowserTransportAsync(
            data.Path,
            rawCapability,
            "/Account/Activate",
            "Password",
            "ConfirmPassword",
            "Activate account");
    }

    [Fact]
    public async Task OwnerRecoveryFragmentIsScrubbedAndSentOnlyInPostBody()
    {
        using TestDataDirectory data = new();
        string rawCapability = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("browser-owner-recovery")));
        await using (ServiceProvider provider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(provider);
            await InitializeOwnerAsync(provider, "recovery-browser-bootstrap");
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<OwnerRecoveryIssuer>()
                .IssueAsync(SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability)));
        }

        await AssertCapabilityBrowserTransportAsync(
            data.Path,
            rawCapability,
            "/Account/RecoverOwner",
            "NewPassword",
            "ConfirmPassword",
            "Reset Owner password");
    }

    [Fact]
    public async Task CapabilityFragmentIsScrubbedAndSentOnlyInSetupPostBody()
    {
        using TestDataDirectory data = new();
        string repositoryRoot = FindRepositoryRoot();
        string applicationAssembly = typeof(Program).Assembly.Location;
        string rawCapability = await GenerateCapabilityAsync(
            repositoryRoot,
            applicationAssembly,
            data.Path);
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}");
        using Process host = StartHost(
            repositoryRoot,
            applicationAssembly,
            data.Path,
            origin);
        Task hostOutput = host.StandardOutput.ReadToEndAsync();
        Task hostError = host.StandardError.ReadToEndAsync();

        try
        {
            await WaitForHostAsync(origin, host);
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });
            IPage page = await browser.NewPageAsync();
            bool capabilitySeenInPostBody = false;
            bool capabilitySeenInRequestPathOrQuery = false;
            bool capabilitySeenInRequestHeaders = false;
            page.Request += (_, request) =>
            {
                Uri requestUri = new(request.Url);
                capabilitySeenInRequestPathOrQuery |=
                    requestUri.AbsolutePath.Contains(rawCapability, StringComparison.Ordinal)
                    || requestUri.Query.Contains(rawCapability, StringComparison.Ordinal);
                capabilitySeenInRequestHeaders |= request.Headers.Values.Any(
                    value => value.Contains(rawCapability, StringComparison.Ordinal));
                if (request.Method == HttpMethod.Post.Method
                    && requestUri.AbsolutePath == "/Setup"
                    && request.PostData?.Contains(rawCapability, StringComparison.Ordinal) == true)
                {
                    capabilitySeenInPostBody = true;
                }
            };

            await page.GotoAsync(
                $"{origin.AbsoluteUri}Setup#token={rawCapability}",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                });

            string currentUrl = page.Url;
            string capabilityValue = await page
                .Locator("#Capability")
                .InputValueAsync();
            string? capabilityAttribute = await page
                .Locator("#Capability")
                .GetAttributeAsync("value");
            int localStorageCount = await page.EvaluateAsync<int>("() => localStorage.length");
            int sessionStorageCount = await page.EvaluateAsync<int>("() => sessionStorage.length");

            Assert.False(currentUrl.Contains(rawCapability, StringComparison.Ordinal));
            Assert.DoesNotContain("#", currentUrl, StringComparison.Ordinal);
            Assert.True(FixedTimeEquals(rawCapability, capabilityValue));
            Assert.Null(capabilityAttribute);
            Assert.Equal(0, localStorageCount);
            Assert.Equal(0, sessionStorageCount);

            await page.Locator("#UserName").FillAsync("browser-owner");
            await page.Locator("#Password").FillAsync(ValidPassword);
            await page.Locator("#ConfirmPassword").FillAsync(ValidPassword);
            await Task.WhenAll(
                page.WaitForURLAsync("**/Login"),
                page.GetByRole(AriaRole.Button, new() { Name = "Create Owner" }).ClickAsync());

            Assert.True(capabilitySeenInPostBody);
            Assert.False(capabilitySeenInRequestPathOrQuery);
            Assert.False(capabilitySeenInRequestHeaders);
            Assert.False(page.Url.Contains(rawCapability, StringComparison.Ordinal));
            Assert.False(
                (await page.ContentAsync()).Contains(rawCapability, StringComparison.Ordinal));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => sessionStorage.length"));

            await page.GoBackAsync(
                new PageGoBackOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                });
            Assert.False(page.Url.Contains(rawCapability, StringComparison.Ordinal));
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync();
            }

            await Task.WhenAll(hostOutput, hostError);
        }
    }

    private static async Task AssertCapabilityBrowserTransportAsync(
        string dataDirectory,
        string rawCapability,
        string route,
        string passwordField,
        string confirmationField,
        string buttonName)
    {
        string repositoryRoot = FindRepositoryRoot();
        string applicationAssembly = typeof(Program).Assembly.Location;
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}");
        using Process host = StartHost(
            repositoryRoot,
            applicationAssembly,
            dataDirectory,
            origin);
        Task hostOutput = host.StandardOutput.ReadToEndAsync();
        Task hostError = host.StandardError.ReadToEndAsync();

        try
        {
            await WaitForHostAsync(origin, host, route);
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });
            IPage page = await browser.NewPageAsync();
            bool capabilitySeenInPostBody = false;
            bool capabilitySeenInPathQueryOrHeaders = false;
            page.Request += (_, request) =>
            {
                Uri requestUri = new(request.Url);
                capabilitySeenInPathQueryOrHeaders |=
                    requestUri.AbsolutePath.Contains(rawCapability, StringComparison.Ordinal)
                    || requestUri.Query.Contains(rawCapability, StringComparison.Ordinal)
                    || request.Headers.Values.Any(
                        value => value.Contains(rawCapability, StringComparison.Ordinal));
                if (request.Method == HttpMethod.Post.Method
                    && requestUri.AbsolutePath == route
                    && request.PostData?.Contains(rawCapability, StringComparison.Ordinal) == true)
                {
                    capabilitySeenInPostBody = true;
                }
            };

            await page.GotoAsync(
                $"{origin.AbsoluteUri.TrimEnd('/')}{route}#token={rawCapability}",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                });
            Assert.DoesNotContain(rawCapability, page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("#", page.Url, StringComparison.Ordinal);
            Assert.True(
                FixedTimeEquals(
                    rawCapability,
                    await page.Locator("#Capability").InputValueAsync()));
            Assert.Null(
                await page.Locator("#Capability").GetAttributeAsync("value"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => sessionStorage.length"));

            await page.Locator($"#{passwordField}").FillAsync(ValidPassword);
            await page.Locator($"#{confirmationField}").FillAsync(ValidPassword);
            await Task.WhenAll(
                page.WaitForURLAsync("**/Login"),
                page.GetByRole(
                    AriaRole.Button,
                    new() { Name = buttonName }).ClickAsync());

            Assert.True(capabilitySeenInPostBody);
            Assert.False(capabilitySeenInPathQueryOrHeaders);
            Assert.DoesNotContain(rawCapability, page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain(
                rawCapability,
                await page.ContentAsync(),
                StringComparison.Ordinal);
            Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => sessionStorage.length"));
            await page.GoBackAsync(
                new PageGoBackOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                });
            Assert.DoesNotContain(rawCapability, page.Url, StringComparison.Ordinal);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync();
            }

            await Task.WhenAll(hostOutput, hostError);
        }
    }

    private static async Task<Guid> InitializeOwnerAsync(
        ServiceProvider provider,
        string capabilitySeed)
    {
        string rawCapability = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes(capabilitySeed)));
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(SHA256.HashData(Encoding.UTF8.GetBytes(rawCapability)));
        Assert.Equal(
            InitialOwnerSetupStatus.Succeeded,
            (await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        rawCapability,
                        "owner-local",
                        null,
                        ValidPassword))).Status);
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Select(user => user.Id)
            .SingleAsync();
    }

    private static async Task<string> GenerateCapabilityAsync(
        string repositoryRoot,
        string applicationAssembly,
        string dataDirectory)
    {
        using Process process = StartProcess(
            repositoryRoot,
            applicationAssembly,
            dataDirectory,
            "bootstrap-owner");
        string output = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        string[] lines = output.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("/Setup", lines[0]);
        Assert.Equal(43, lines[1].Length);
        return lines[1];
    }

    private static Process StartHost(
        string repositoryRoot,
        string applicationAssembly,
        string dataDirectory,
        Uri origin)
    {
        ProcessStartInfo startInfo = CreateStartInfo(
            repositoryRoot,
            applicationAssembly,
            dataDirectory,
            "--contentRoot",
            Path.Combine(repositoryRoot, "src", "TemperedTyrant.CreatorToolkit.Web"));
        startInfo.Environment["ASPNETCORE_URLS"] = origin.AbsoluteUri;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The application host could not start.");
    }

    private static Process StartProcess(
        string repositoryRoot,
        string applicationAssembly,
        string dataDirectory,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = CreateStartInfo(
            repositoryRoot,
            applicationAssembly,
            dataDirectory,
            arguments);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The application process could not start.");
    }

    private static ProcessStartInfo CreateStartInfo(
        string repositoryRoot,
        string applicationAssembly,
        string dataDirectory,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(applicationAssembly);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CREATOR_TOOLKIT_DataDirectory"] = dataDirectory;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        return startInfo;
    }

    private static async Task WaitForHostAsync(
        Uri origin,
        Process host,
        string readyPath = "/Setup")
    {
        using HttpClient client = new()
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(1),
        };
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (host.HasExited)
            {
                throw new InvalidOperationException("The application host exited during startup.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(readyPath);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The application host did not become ready.");
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                directory.FullName,
                "TemperedTyrant.CreatorToolkit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
