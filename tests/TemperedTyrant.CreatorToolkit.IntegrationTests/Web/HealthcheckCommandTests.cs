using System.Diagnostics;
using System.Net;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Commands;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class HealthcheckCommandTests
{
    [Fact]
    public async Task CommandUsesOnlyTheFixedContainerLocalLivenessEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        using StubHttpMessageHandler handler = new(
            request =>
            {
                capturedRequest = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
        using HttpMessageInvoker client = new(handler, disposeHandler: false);

        int exitCode = await HealthcheckCommand.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/live"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
        Assert.Equal(
            "http://127.0.0.1:8080/health/live",
            capturedRequest.RequestUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task CommandFailsForEveryNonOkStatus(HttpStatusCode statusCode)
    {
        using StubHttpMessageHandler handler = new(
            _ => Task.FromResult(new HttpResponseMessage(statusCode)));
        using HttpMessageInvoker client = new(handler, disposeHandler: false);

        int exitCode = await HealthcheckCommand.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/live"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task CommandAppliesItsBoundAndReturnsNoExceptionDetails()
    {
        using StubHttpMessageHandler handler = new(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using HttpMessageInvoker client = new(handler, disposeHandler: false);

        int exitCode = await HealthcheckCommand.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/live"),
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task CommandFailsClosedForUnexpectedTransportFailure()
    {
        using StubHttpMessageHandler handler = new(
            _ => throw new InvalidOperationException("sensitive transport detail"));
        using HttpMessageInvoker client = new(handler, disposeHandler: false);

        int exitCode = await HealthcheckCommand.RunAsync(
            client,
            new Uri("http://127.0.0.1:8080/health/live"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task CommandDispatchDoesNotInitializeTheApplicationDataDirectory()
    {
        using TestDataDirectory root = new();
        string untouchedPath = Path.Combine(root.Path, "healthcheck-must-not-touch");
        string applicationAssembly = typeof(Program).Assembly.Location;
        ProcessStartInfo startInfo = new(Environment.ProcessPath!)
        {
            Arguments = $"\"{applicationAssembly}\" {HealthcheckCommand.Name}",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CREATOR_TOOLKIT_DataDirectory"] = untouchedPath;

        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.False(Directory.Exists(untouchedPath));
        Assert.Empty(output);
        Assert.Empty(error);
    }

    [Fact]
    public async Task UnavailableDataDirectoryProducesOnlyFixedStartupFailure()
    {
        using TestDataDirectory root = new();
        string unavailablePath = Path.Combine(root.Path, "not-a-directory");
        await File.WriteAllTextAsync(unavailablePath, "blocking file");
        string applicationAssembly = typeof(Program).Assembly.Location;
        ProcessStartInfo startInfo = new(Environment.ProcessPath!)
        {
            Arguments = $"\"{applicationAssembly}\"",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CREATOR_TOOLKIT_DataDirectory"] = unavailablePath;

        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(output);
        Assert.Equal($"Application startup failed.{Environment.NewLine}", error);
        Assert.DoesNotContain(unavailablePath, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidForwardingConfigurationProducesOnlyFixedStartupFailure()
    {
        using TestDataDirectory root = new();
        const string marker = "private-proxy-marker-3092";
        string applicationAssembly = typeof(Program).Assembly.Location;
        ProcessStartInfo startInfo = new(Environment.ProcessPath!)
        {
            Arguments = $"\"{applicationAssembly}\"",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CREATOR_TOOLKIT_DataDirectory"] =
            Path.Combine(root.Path, "unused-data");
        startInfo.Environment["CREATOR_TOOLKIT_TrustedProxies"] = marker;

        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(output);
        Assert.Equal($"Application startup failed.{Environment.NewLine}", error);
        Assert.DoesNotContain(marker, error, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, error, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _send;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
            : this((request, _) => send(request))
        {
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _send(request, cancellationToken);
        }
    }
}
