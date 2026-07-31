using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Infrastructure.Health;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.Health;
using TemperedTyrant.CreatorToolkit.Web.Hosting;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class HealthEndpointTests
{
    private const string ValidPassword = "mild river orbit velvet canyon";

    [Fact]
    public async Task LivenessIsFixedAnonymousAndDoesNotInvokeInfrastructureReadiness()
    {
        List<string> logs = [];
        CountingAuthenticationService authentication = new();
        MutableDataProtectionValidator dataProtection = new();
        ControlledInfrastructureProbe probe = new(
            _ => throw new InvalidOperationException("liveness-probe-marker"));
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IInfrastructureReadinessProbe>();
                services.AddSingleton<IInfrastructureReadinessProbe>(probe);
                services.RemoveAll<IAuthenticationService>();
                services.AddSingleton<IAuthenticationService>(authentication);
                services.RemoveAll<IDataProtectionValidator>();
                services.AddSingleton<IDataProtectionValidator>(dataProtection);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        authentication.Reset();
        dataProtection.Reset();
        logs.Clear();
        factory.Services
            .GetRequiredService<PersistenceInitializationState>()
            .MarkFailed();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            "creator-toolkit-auth=untrusted-cookie-marker");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.25");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.OK,
            "live");
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, authentication.CallCount);
        Assert.Equal(0, dataProtection.CallCount);
        Assert.Empty(logs);
        Assert.Equal(0, await CountDiagnosticsAsync(factory.Services));
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task UnsupportedMethodsDoNotExecuteHealthProbes(string method)
    {
        ControlledInfrastructureProbe probe = new(_ => Task.FromResult(true));
        await using CreatorToolkitWebFactory factory = CreateFactoryWithProbe(probe);
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        foreach (string path in new[] { "/health/live", "/health/ready" })
        {
            using HttpRequestMessage request = new(new HttpMethod(method), path);
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task ReadinessExecutesBeforeAuthenticationEvenWithRequestHeaders()
    {
        CountingAuthenticationService authentication = new();
        ControlledInfrastructureProbe probe = new(_ => Task.FromResult(true));
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IInfrastructureReadinessProbe>();
                services.AddSingleton<IInfrastructureReadinessProbe>(probe);
                services.RemoveAll<IAuthenticationService>();
                services.AddSingleton<IAuthenticationService>(authentication);
            });
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        authentication.Reset();
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/ready");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            "creator-toolkit-auth=untrusted-cookie-marker");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.25");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedResponseAsync(response, HttpStatusCode.OK, "ready");
        Assert.Equal(0, authentication.CallCount);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ReadinessIsHealthyForValidUninitializedInstallation()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(response, HttpStatusCode.OK, "ready");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Null((await db.InstallationStates.SingleAsync()).InitializedAtUtc);
    }

    [Fact]
    public async Task ReadinessIsHealthyForValidInitializedInstallation()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await InitializeInstallationAsync(factory.Services);

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(response, HttpStatusCode.OK, "ready");
    }

    [Fact]
    public async Task ReadinessRejectsIncompleteAndFailedPersistenceStartup()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient();
        PersistenceInitializationState state = factory.Services
            .GetRequiredService<PersistenceInitializationState>();

        state.MarkRunning();
        using HttpResponseMessage incomplete = await client.GetAsync("/health/ready");
        await AssertFixedResponseAsync(
            incomplete,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");

        state.MarkFailed();
        using HttpResponseMessage failed = await client.GetAsync("/health/ready");
        await AssertFixedResponseAsync(
            failed,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
    }

    [Fact]
    public async Task ReadinessRejectsUnavailableDatabase()
    {
        List<string> logs = [];
        await using CreatorToolkitWebFactory factory = new(
            services => services.AddLogging(
                logging => logging.AddProvider(new TestLoggerProvider(logs))));
        using HttpClient client = factory.CreateClient();
        _ = await client.GetAsync("/health/ready");
        DataDirectoryLayout layout = factory.Services
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;

        SqliteConnection.ClearAllPools();
        string displacedDatabase = Path.Combine(factory.DataDirectory, "unavailable-database");
        File.Move(layout.DatabasePath, displacedDatabase);
        Directory.CreateDirectory(layout.DatabasePath);
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        Assert.Empty(logs);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(factory.DataDirectory, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessRejectsPendingMigrationWithoutApplyingIt()
    {
        List<string> logs = [];
        await using CreatorToolkitWebFactory factory = new(
            services => services.AddLogging(
                logging => logging.AddProvider(new TestLoggerProvider(logs))));
        using HttpClient client = factory.CreateClient();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = scope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";");
        }
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        await using AsyncServiceScope verificationScope =
            factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext verification = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.True((await verification.Database.GetPendingMigrationsAsync()).Any());
        Assert.Empty(logs);
        Assert.Equal(0, await verification.DiagnosticRecords.CountAsync());
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("__EFMigrationsHistory", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessRejectsUnusableDataProtectionState()
    {
        List<string> logs = [];
        MutableDataProtectionValidator validator = new();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IDataProtectionValidator>();
                services.AddSingleton<IDataProtectionValidator>(validator);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        using HttpClient client = factory.CreateClient();
        validator.IsAvailable = false;
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        Assert.Empty(logs);
        Assert.DoesNotContain(factory.DataDirectory, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ApplicationLifecycleState.Starting)]
    [InlineData(ApplicationLifecycleState.Stopping)]
    [InlineData(ApplicationLifecycleState.Stopped)]
    [InlineData(ApplicationLifecycleState.Failed)]
    public async Task ReadinessRejectsNonRunningLifecycle(
        ApplicationLifecycleState lifecycleState)
    {
        await using CreatorToolkitWebFactory factory = new(
            services => ReplaceReadinessLifecycle(services, lifecycleState));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
    }

    [Fact]
    public async Task ReadinessRejectsShutdownInProgress()
    {
        using TestHostApplicationLifetime stoppingLifetime = new();
        stoppingLifetime.StopApplication();
        await using CreatorToolkitWebFactory factory = new(
            services => ReplaceReadinessLifetime(services, stoppingLifetime));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
    }

    [Theory]
    [InlineData(ReadinessRaceTransition.LifecycleStopping)]
    [InlineData(ReadinessRaceTransition.LifecycleFailed)]
    [InlineData(ReadinessRaceTransition.ApplicationStopping)]
    public async Task ProbeSuccessIsRejectedWhenRuntimeStopsDuringProbe(
        ReadinessRaceTransition transition)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledInfrastructureProbe probe = new(
            async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return true;
            });
        using TestHostApplicationLifetime readinessLifetime = new();
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IInfrastructureReadinessProbe>();
                services.AddSingleton<IInfrastructureReadinessProbe>(probe);
                ReplaceReadinessLifetime(services, readinessLifetime);
            });
        using HttpClient client = factory.CreateClient();
        ApplicationLifecycleCoordinator coordinator = factory.Services
            .GetRequiredService<ApplicationLifecycleCoordinator>();
        Task<HttpResponseMessage> request = client.GetAsync("/health/ready");
        await probe.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        switch (transition)
        {
            case ReadinessRaceTransition.LifecycleStopping:
                coordinator.SignalStopping();
                break;
            case ReadinessRaceTransition.LifecycleFailed:
                coordinator.SignalStopping();
                Assert.True(coordinator.TryClaimShutdown(out long generation));
                coordinator.MarkFailed(generation);
                break;
            case ReadinessRaceTransition.ApplicationStopping:
                readinessLifetime.StopApplication();
                break;
            default:
                throw new InvalidOperationException("Unknown readiness race transition.");
        }

        release.TrySetResult();
        using HttpResponseMessage response = await request;

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ReadinessTimeoutIsBoundedAndReturnsNotReady()
    {
        using ManualResetEventSlim release = new();
        int attemptedOperations = 0;
        ControlledInfrastructureProbe probe = new(
            _ =>
            {
                if (Interlocked.Increment(ref attemptedOperations) == 1)
                {
                    release.Wait(CancellationToken.None);
                    throw new InvalidOperationException("late-health-failure-marker");
                }

                return Task.FromResult(true);
            });
        await using CreatorToolkitWebFactory factory = CreateFactoryWithProbe(
            probe,
            TimeSpan.FromMilliseconds(100));
        using HttpClient client = factory.CreateClient();
        Stopwatch stopwatch = Stopwatch.StartNew();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");
        stopwatch.Stop();

        await AssertFixedResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(1, probe.ActiveCallCount);
        Assert.Equal(1, probe.MaximumActiveCallCount);

        using HttpResponseMessage repeated = await client.GetAsync("/health/ready");
        await AssertFixedResponseAsync(
            repeated,
            HttpStatusCode.ServiceUnavailable,
            "not_ready");
        Assert.Equal(1, probe.CallCount);

        release.Set();
        await probe.AllCallsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, probe.ActiveCallCount);

        using HttpResponseMessage afterCompletion = await WaitForReadyAsync(client);
        await AssertFixedResponseAsync(afterCompletion, HttpStatusCode.OK, "ready");
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task ConcurrentPollingCoalescesProbeWork()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledInfrastructureProbe probe = new(
            async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return true;
            });
        await using CreatorToolkitWebFactory factory = CreateFactoryWithProbe(probe);
        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage>[] requests = Enumerable
            .Range(0, 32)
            .Select(_ => client.GetAsync("/health/ready"))
            .ToArray();
        await probe.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        release.TrySetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(1, probe.CallCount);
            Assert.Equal(1, probe.MaximumActiveCallCount);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task RequestCancellationStopsOnlyItsCallerWhileSharedWorkCompletes()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledInfrastructureProbe probe = new(
            async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return true;
            });
        await using CreatorToolkitWebFactory factory = CreateFactoryWithProbe(
            probe,
            TimeSpan.FromSeconds(1));
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource requestCancellation = new();
        Task<HttpResponseMessage> cancelledRequest = client.GetAsync(
            "/health/ready",
            requestCancellation.Token);
        await probe.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<HttpResponseMessage> survivingRequest = client.GetAsync("/health/ready");
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
        release.TrySetResult();
        using HttpResponseMessage survivingResponse = await survivingRequest;
        await probe.AllCallsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await AssertFixedResponseAsync(
            survivingResponse,
            HttpStatusCode.OK,
            "ready");
        Assert.Equal(0, await CountDiagnosticsAsync(factory.Services));
        Assert.Equal(0, probe.ActiveCallCount);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task FailuresAreFixedRedactedAndDoNotPersistDiagnosticsOrLogs()
    {
        const string sensitiveMarker = "health-sensitive-path-and-key-marker";
        List<string> logs = [];
        ControlledInfrastructureProbe probe = new(
            _ => throw new InvalidOperationException(sensitiveMarker));
        await using CreatorToolkitWebFactory factory = new(
            services =>
            {
                services.RemoveAll<IInfrastructureReadinessProbe>();
                services.AddSingleton<IInfrastructureReadinessProbe>(probe);
                services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(logs)));
            });
        using HttpClient client = factory.CreateClient();
        logs.Clear();

        for (int index = 0; index < 25; index++)
        {
            using HttpResponseMessage response = await client.GetAsync("/health/ready");
            await AssertFixedResponseAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "not_ready");
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(sensitiveMarker, body, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountDiagnosticsAsync(factory.Services));
        Assert.Empty(logs);
        Assert.DoesNotContain(
            logs,
            message => message.Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedPollingDoesNotCreateDiagnosticsKeyChurnOrModelDrift()
    {
        List<string> logs = [];
        await using CreatorToolkitWebFactory factory = new(
            services => services.AddLogging(
                logging => logging.AddProvider(new TestLoggerProvider(logs))));
        using HttpClient client = factory.CreateClient();
        DataDirectoryLayout layout = factory.Services
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        int originalKeyFileCount = Directory.EnumerateFiles(layout.KeyRingPath).Count();
        logs.Clear();

        for (int index = 0; index < 50; index++)
        {
            using HttpResponseMessage response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        for (int round = 0; round < 5; round++)
        {
            Task<HttpResponseMessage>[] requests = Enumerable
                .Range(0, 16)
                .Select(_ => client.GetAsync("/health/ready"))
                .ToArray();
            HttpResponseMessage[] responses = await Task.WhenAll(requests);
            foreach (HttpResponseMessage response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                response.Dispose();
            }
        }

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.False((await db.Database.GetPendingMigrationsAsync()).Any());
        Assert.Equal(
            originalKeyFileCount,
            Directory.EnumerateFiles(layout.KeyRingPath).Count());
        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());
        Assert.Empty(logs);
    }

    private static CreatorToolkitWebFactory CreateFactoryWithProbe(
        IInfrastructureReadinessProbe probe,
        TimeSpan? timeout = null)
    {
        return new CreatorToolkitWebFactory(
            services =>
            {
                services.RemoveAll<IInfrastructureReadinessProbe>();
                services.AddSingleton(probe);
                if (timeout is not null)
                {
                    services.RemoveAll<HealthReadinessOptions>();
                    services.AddSingleton(new HealthReadinessOptions(timeout.Value));
                }
            });
    }

    private static void ReplaceReadinessLifecycle(
        IServiceCollection services,
        ApplicationLifecycleState state)
    {
        ApplicationLifecycleCoordinator coordinator = CreateLifecycle(state);
        services.RemoveAll<ApplicationReadinessService>();
        services.AddSingleton(
            provider => new ApplicationReadinessService(
                provider.GetRequiredService<CreatorToolkitOptions>(),
                provider.GetRequiredService<IInfrastructureReadinessProbe>(),
                coordinator,
                provider.GetRequiredService<IHostApplicationLifetime>(),
                provider.GetRequiredService<HealthReadinessOptions>(),
                provider.GetRequiredService<TimeProvider>()));
    }

    private static void ReplaceReadinessLifetime(
        IServiceCollection services,
        IHostApplicationLifetime applicationLifetime)
    {
        services.RemoveAll<ApplicationReadinessService>();
        services.AddSingleton(
            provider => new ApplicationReadinessService(
                provider.GetRequiredService<CreatorToolkitOptions>(),
                provider.GetRequiredService<IInfrastructureReadinessProbe>(),
                provider.GetRequiredService<ApplicationLifecycleCoordinator>(),
                applicationLifetime,
                provider.GetRequiredService<HealthReadinessOptions>(),
                provider.GetRequiredService<TimeProvider>()));
    }

    private static ApplicationLifecycleCoordinator CreateLifecycle(
        ApplicationLifecycleState state)
    {
        ApplicationLifecycleCoordinator coordinator = new();
        if (state == ApplicationLifecycleState.Starting)
        {
            return coordinator;
        }

        long generation = coordinator.BeginStartup();
        Assert.True(coordinator.TryMarkRunning(generation));
        if (state == ApplicationLifecycleState.Running)
        {
            return coordinator;
        }

        if (state == ApplicationLifecycleState.Failed)
        {
            coordinator.MarkFailed(generation);
            return coordinator;
        }

        coordinator.SignalStopping();
        if (state == ApplicationLifecycleState.Stopping)
        {
            return coordinator;
        }

        Assert.True(coordinator.TryClaimShutdown(out long shutdownGeneration));
        Assert.True(coordinator.TryMarkStopped(shutdownGeneration));
        return coordinator;
    }

    private static async Task InitializeInstallationAsync(IServiceProvider services)
    {
        string capability = WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(32));
        byte[] capabilityHash = SHA256.HashData(Encoding.UTF8.GetBytes(capability));

        await using (AsyncServiceScope issueScope = services.CreateAsyncScope())
        {
            BootstrapCapabilityIssueResult issueResult = await issueScope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>()
                .IssueAsync(capabilityHash);
            Assert.Equal(BootstrapCapabilityIssueResult.Created, issueResult);
        }

        await using AsyncServiceScope setupScope = services.CreateAsyncScope();
        InitialOwnerSetupResult setupResult = await setupScope.ServiceProvider
            .GetRequiredService<InitialOwnerSetupService>()
            .CreateAsync(
                new InitialOwnerSetupRequest(
                    capability,
                    "health-test-owner",
                    null,
                    ValidPassword));
        Assert.Equal(InitialOwnerSetupStatus.Succeeded, setupResult.Status);
    }

    private static async Task<int> CountDiagnosticsAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        return await db.DiagnosticRecords.CountAsync();
    }

    private static async Task<HttpResponseMessage> WaitForReadyAsync(HttpClient client)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        while (true)
        {
            HttpResponseMessage response = await client.GetAsync(
                "/health/ready",
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task AssertFixedResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedValue)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(
            "default-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonProperty property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal(expectedValue, property.Value.GetString());
    }

    private sealed class MutableDataProtectionValidator : IDataProtectionValidator
    {
        private int _callCount;

        public bool IsAvailable { get; set; } = true;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void Reset() => Volatile.Write(ref _callCount, 0);

        public bool IsUsable()
        {
            Interlocked.Increment(ref _callCount);
            return IsAvailable;
        }

        public Task<bool> IsUsableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(IsAvailable);
        }
    }

    private sealed class CountingAuthenticationService : IAuthenticationService
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void Reset() => Volatile.Write(ref _callCount, 0);

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref _callCount);
            return Task.CompletedTask;
        }

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref _callCount);
            return Task.CompletedTask;
        }

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref _callCount);
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            Interlocked.Increment(ref _callCount);
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledInfrastructureProbe(
        Func<CancellationToken, Task<bool>> probe) : IInfrastructureReadinessProbe
    {
        private int _activeCallCount;
        private int _callCount;
        private int _maximumActiveCallCount;

        internal int ActiveCallCount => Volatile.Read(ref _activeCallCount);

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int MaximumActiveCallCount => Volatile.Read(ref _maximumActiveCallCount);

        internal TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllCallsCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            int active = Interlocked.Increment(ref _activeCallCount);
            int observedMaximum;
            do
            {
                observedMaximum = Volatile.Read(ref _maximumActiveCallCount);
                if (active <= observedMaximum)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                ref _maximumActiveCallCount,
                active,
                observedMaximum) != observedMaximum);
            FirstCallStarted.TrySetResult();

            try
            {
                return await probe(cancellationToken);
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeCallCount) == 0)
                {
                    AllCallsCompleted.TrySetResult();
                }
            }
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    public enum ReadinessRaceTransition
    {
        LifecycleStopping = 1,
        LifecycleFailed = 2,
        ApplicationStopping = 3,
    }
}
