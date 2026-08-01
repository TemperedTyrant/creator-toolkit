using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AnnouncementBrowserTests
{
    private const string OwnerPassword = "mild river orbit velvet canyon";
    private const string ViewerPassword = "silver meadow lantern compass";

    [Fact]
    public async Task AuthorAndViewerCompleteAccessibleDraftManagementJourney()
    {
        using TestDataDirectory data = new();
        await InitializeAccountsAsync(data.Path);
        string repositoryRoot = FindRepositoryRoot();
        string applicationAssembly = typeof(Program).Assembly.Location;
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
                new BrowserTypeLaunchOptions { Headless = true });
            IPage page = await browser.NewPageAsync();

            await LoginAsync(page, origin, "owner-local", OwnerPassword);
            await page.GetByRole(
                AriaRole.Link,
                new() { Name = "Announcements", Exact = true }).ClickAsync();
            await page.GetByRole(
                AriaRole.Link,
                new() { Name = "Create announcement" }).ClickAsync();

            string preservedBody = "Browser validation content";
            await page.Locator("#Title").FillAsync("   ");
            await page.Locator("#Body").FillAsync(preservedBody);
            await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
            Assert.True(
                await page.Locator("[data-valmsg-for='Title']").IsVisibleAsync());
            Assert.True(
                FixedTimeEquals(
                    preservedBody,
                    await page.Locator("#Body").InputValueAsync()));

            string title = "Browser-managed draft";
            string body = "First browser paragraph\n\nSecond browser paragraph";
            await page.Locator("#Title").FillAsync(title);
            await page.Locator("#Body").FillAsync(body);
            await Task.WhenAll(
                page.WaitForURLAsync("**/Announcements/*?notice=created"),
                page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync());
            Assert.Equal(1, await page.GetByRole(AriaRole.Heading, new() { Name = title }).CountAsync());
            Assert.True(
                FixedTimeEquals(
                    body,
                    ((await page.Locator(".announcement-body").TextContentAsync())!)
                        .Replace("\r\n", "\n", StringComparison.Ordinal)));

            await page.GetByRole(AriaRole.Link, new() { Name = "Edit draft" }).ClickAsync();
            string editedTitle = "Edited browser draft";
            await page.Locator("#Title").FillAsync(editedTitle);
            await Task.WhenAll(
                page.WaitForURLAsync("**/Announcements/*?notice=updated"),
                page.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync());

            await Task.WhenAll(
                page.WaitForURLAsync("**/Announcements/*?notice=archived"),
                page.GetByRole(AriaRole.Button, new() { Name = "Archive" }).ClickAsync());
            await page.GetByRole(
                AriaRole.Link,
                new() { Name = "Back to announcements" }).ClickAsync();
            await page.Locator("#Status").SelectOptionAsync("Archived");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();
            Assert.Equal(1, await page.GetByRole(AriaRole.Link, new() { Name = editedTitle }).CountAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = editedTitle }).ClickAsync();
            await Task.WhenAll(
                page.WaitForURLAsync("**/Announcements/*?notice=restored"),
                page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Restore to Draft" }).ClickAsync());

            await SignOutAsync(page);
            await LoginAsync(page, origin, "announcement-viewer", ViewerPassword);
            await page.GotoAsync(new Uri(origin, "/Announcements").AbsoluteUri);
            await page.GetByRole(AriaRole.Link, new() { Name = editedTitle }).ClickAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Edit draft" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Archive" }).CountAsync());
            Assert.Equal(
                0,
                await page.GetByRole(
                    AriaRole.Link,
                    new() { Name = "Delete permanently" }).CountAsync());

            await SignOutAsync(page);
            await LoginAsync(page, origin, "owner-local", OwnerPassword);
            await page.GotoAsync(new Uri(origin, "/Announcements").AbsoluteUri);
            await page.GetByRole(AriaRole.Link, new() { Name = editedTitle }).ClickAsync();
            await page.GetByRole(
                AriaRole.Link,
                new() { Name = "Delete permanently" }).ClickAsync();
            await Task.WhenAll(
                page.WaitForURLAsync("**/Announcements?notice=deleted"),
                page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Permanently delete" }).ClickAsync());
            Assert.True(
                await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = "No announcements found" }).IsVisibleAsync());
            Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => sessionStorage.length"));
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

    private static async Task InitializeAccountsAsync(string dataDirectory)
    {
        await using ServiceProvider provider = TestServices.Create(dataDirectory);
        await TestServices.InitializeAsync(provider);
        string rawCapability = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("announcement-browser-bootstrap")));
        Guid ownerId;
        await using (AsyncServiceScope ownerScope = provider.CreateAsyncScope())
        {
            await ownerScope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>()
                .IssueAsync(Hash(rawCapability));
            Assert.Equal(
                InitialOwnerSetupStatus.Succeeded,
                (await ownerScope.ServiceProvider
                    .GetRequiredService<InitialOwnerSetupService>()
                    .CreateAsync(
                        new InitialOwnerSetupRequest(
                            rawCapability,
                            "owner-local",
                            "Owner",
                            OwnerPassword))).Status);
            ownerId = await ownerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .Users
                .Select(value => value.Id)
                .SingleAsync();
        }

        UserLifecycleResult pending;
        await using (AsyncServiceScope createScope = provider.CreateAsyncScope())
        {
            pending = await createScope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .CreatePendingAsync(
                    ownerId,
                    "announcement-viewer",
                    "Viewer",
                    SystemRoles.Viewer);
            Assert.Equal(UserLifecycleStatus.Succeeded, pending.Status);
        }

        await using AsyncServiceScope activationScope = provider.CreateAsyncScope();
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await activationScope.ServiceProvider
                .GetRequiredService<AccountActivationService>()
                .ActivateAsync(
                    pending.OneTimeActivationCapability!,
                    ViewerPassword)).Status);
    }

    private static async Task LoginAsync(
        IPage page,
        Uri origin,
        string userName,
        string password)
    {
        await page.GotoAsync(new Uri(origin, "/Login").AbsoluteUri);
        await page.Locator("#UserName").FillAsync(userName);
        await page.Locator("#Password").FillAsync(password);
        await Task.WhenAll(
            page.WaitForURLAsync("**/Dashboard"),
            page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync());
    }

    private static async Task SignOutAsync(IPage page)
    {
        await Task.WhenAll(
            page.WaitForURLAsync("**/Login"),
            page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync());
    }

    private static Process StartHost(
        string repositoryRoot,
        string applicationAssembly,
        string dataDirectory,
        Uri origin)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(applicationAssembly);
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(
            Path.Combine(
                repositoryRoot,
                "src",
                "TemperedTyrant.CreatorToolkit.Web"));
        startInfo.Environment["CREATOR_TOOLKIT_DataDirectory"] = dataDirectory;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = origin.AbsoluteUri;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The application host could not start.");
    }

    private static async Task WaitForHostAsync(Uri origin, Process host)
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
                using HttpResponseMessage response = await client.GetAsync("/health/ready");
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

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
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
