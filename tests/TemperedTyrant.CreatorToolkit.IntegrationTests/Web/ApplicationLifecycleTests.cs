using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Hosting;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class ApplicationLifecycleTests
{
    [Fact]
    public async Task NormalLifecycleTransitionsInOrderAndStopsAcceptingWork()
    {
        ApplicationLifecycleCoordinator coordinator = new();
        RecordingLogger logger = new();
        ControllableLifecycleService service = CreateService(coordinator, logger);

        Assert.Equal(ApplicationLifecycleState.Starting, coordinator.GetStatus().State);
        Assert.False(coordinator.GetStatus().AcceptingLifecycleWork);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationLifecycleState.Running, coordinator.GetStatus().State);
        Assert.True(coordinator.GetStatus().AcceptingLifecycleWork);

        service.BlockShutdown = true;
        Task stop = service.StopAsync(CancellationToken.None);
        await service.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ApplicationLifecycleState.Stopping, coordinator.GetStatus().State);
        Assert.False(coordinator.GetStatus().AcceptingLifecycleWork);
        service.AllowShutdown.TrySetResult();
        await stop;

        Assert.Equal(ApplicationLifecycleState.Stopped, coordinator.GetStatus().State);
        Assert.False(coordinator.GetStatus().AcceptingLifecycleWork);
        Assert.Equal(
            [
                "Application lifecycle is starting.",
                "Application lifecycle is running.",
                "Application lifecycle is stopping.",
                "Application lifecycle is stopped.",
            ],
            logger.Messages);
    }

    [Fact]
    public async Task StartupFailureAndLateStartupCompletionRemainTerminalAndSanitized()
    {
        ApplicationLifecycleCoordinator failedCoordinator = new();
        RecordingLogger failedLogger = new();
        ControllableLifecycleService failedService = CreateService(
            failedCoordinator,
            failedLogger);
        failedService.StartupFailure = new InvalidOperationException(
            "secret-path-and-configuration-marker");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failedService.StartAsync(CancellationToken.None));
        Assert.Equal("Application lifecycle startup failed.", exception.Message);
        Assert.Equal(ApplicationLifecycleState.Failed, failedCoordinator.GetStatus().State);
        Assert.DoesNotContain(
            failedLogger.Messages,
            message => message.Contains(
                "secret-path-and-configuration-marker",
                StringComparison.Ordinal));

        ApplicationLifecycleCoordinator cancelledCoordinator = new();
        RecordingLogger cancelledLogger = new();
        ControllableLifecycleService cancelledService = CreateService(
            cancelledCoordinator,
            cancelledLogger);
        cancelledService.BlockStartup = true;
        using CancellationTokenSource cancellation = new();
        Task cancelledStartup = cancelledService.StartAsync(cancellation.Token);
        await cancelledService.StartupEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledStartup);
        Assert.Equal(ApplicationLifecycleState.Failed, cancelledCoordinator.GetStatus().State);

        cancelledService.AllowStartup.TrySetResult();
        await cancelledService.StartupExited.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.Equal(ApplicationLifecycleState.Failed, cancelledCoordinator.GetStatus().State);
        Assert.False(cancelledCoordinator.GetStatus().AcceptingLifecycleWork);
        Assert.DoesNotContain("Application lifecycle is running.", cancelledLogger.Messages);
    }

    [Fact]
    public async Task CancellationTimeoutAndLateShutdownCompletionRemainFailed()
    {
        ApplicationLifecycleCoordinator cancellationCoordinator = new();
        TestHostApplicationLifetime lifetime = new();
        RecordingLogger cancellationLogger = new();
        ControllableLifecycleService cancellationService = CreateService(
            cancellationCoordinator,
            cancellationLogger,
            lifetime: lifetime);
        cancellationService.BlockShutdown = true;
        cancellationService.IgnoreShutdownCancellation = true;
        await cancellationService.StartAsync(CancellationToken.None);
        using CancellationTokenSource shutdownCancellation = new();
        Task cancellationStop = cancellationService.StopAsync(shutdownCancellation.Token);
        await cancellationService.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        lifetime.StopApplication();
        shutdownCancellation.Cancel();
        await cancellationStop;
        Assert.Equal(ApplicationLifecycleState.Failed, cancellationCoordinator.GetStatus().State);
        Assert.Contains(
            "Application lifecycle shutdown was cancelled.",
            cancellationLogger.Messages);

        ApplicationLifecycleCoordinator timeoutCoordinator = new();
        RecordingLogger timeoutLogger = new();
        ControllableLifecycleService timeoutService = CreateService(
            timeoutCoordinator,
            timeoutLogger,
            TimeSpan.FromMilliseconds(75));
        timeoutService.BlockShutdown = true;
        timeoutService.IgnoreShutdownCancellation = true;
        timeoutService.FailAfterShutdownRelease = true;
        await timeoutService.StartAsync(CancellationToken.None);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await timeoutService.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(ApplicationLifecycleState.Failed, timeoutCoordinator.GetStatus().State);
        Assert.False(timeoutCoordinator.GetStatus().AcceptingLifecycleWork);
        Assert.Contains("Application lifecycle shutdown timed out.", timeoutLogger.Messages);

        timeoutService.AllowShutdown.TrySetResult();
        await timeoutService.ShutdownExited.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.Equal(ApplicationLifecycleState.Failed, timeoutCoordinator.GetStatus().State);
        Assert.False(timeoutCoordinator.GetStatus().AcceptingLifecycleWork);
        Assert.DoesNotContain("Application lifecycle is stopped.", timeoutLogger.Messages);
        Assert.DoesNotContain(
            timeoutLogger.Messages,
            message => message.Contains("late-shutdown-secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentAndRepeatedStopCallsHaveOneShutdownOwner()
    {
        ApplicationLifecycleCoordinator coordinator = new();
        ControllableLifecycleService service = CreateService(
            coordinator,
            new RecordingLogger());
        service.BlockShutdown = true;
        await service.StartAsync(CancellationToken.None);

        Task[] stops = Enumerable
            .Range(0, 32)
            .Select(_ => service.StopAsync(CancellationToken.None))
            .ToArray();
        await service.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, service.ShutdownCallCount);
        Assert.Equal(ApplicationLifecycleState.Stopping, coordinator.GetStatus().State);
        service.AllowShutdown.TrySetResult();
        await Task.WhenAll(stops);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, service.ShutdownCallCount);
        Assert.Equal(ApplicationLifecycleState.Stopped, coordinator.GetStatus().State);
    }

    [Fact]
    public async Task ShutdownFailureIsTerminalAndLoggingIsSanitized()
    {
        ApplicationLifecycleCoordinator coordinator = new();
        RecordingLogger logger = new();
        SynchronousShutdownFailureLifecycleService service = new(
            coordinator,
            new ApplicationLifecycleOptions(TimeSpan.FromSeconds(1)),
            TimeProvider.System,
            new TestHostApplicationLifetime(),
            logger);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(ApplicationLifecycleState.Failed, coordinator.GetStatus().State);
        Assert.False(coordinator.GetStatus().AcceptingLifecycleWork);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("database-path-marker", StringComparison.Ordinal));
        Assert.Contains("Application lifecycle shutdown failed.", logger.Messages);
    }

    [Fact]
    public async Task WebLifecycleStartsAfterPrerequisitesAndHostBoundIsLonger()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/Login")).StatusCode);

        ApplicationLifecycleCoordinator coordinator = factory.Services
            .GetRequiredService<ApplicationLifecycleCoordinator>();
        Assert.Equal(ApplicationLifecycleState.Running, coordinator.GetStatus().State);
        Assert.True(factory.Services.GetRequiredService<ApplicationHostLock>().IsHeld());
        HostOptions hostOptions = factory.Services
            .GetRequiredService<IOptions<HostOptions>>()
            .Value;
        ApplicationLifecycleOptions lifecycleOptions = factory.Services
            .GetRequiredService<ApplicationLifecycleOptions>();
        Assert.True(hostOptions.ShutdownTimeout > lifecycleOptions.ShutdownTimeout);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False((await db.Database.GetPendingMigrationsAsync()).Any());
        DataDirectoryLayout layout = factory.Services
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        Assert.NotEmpty(Directory.EnumerateFiles(layout.KeyRingPath));
    }

    [Fact]
    public async Task ActualHostShutdownBoundsWorkAndReleasesApplicationLock()
    {
        using TestDataDirectory normalData = new();
        CreatorToolkitWebFactory normalFactory = new(dataDirectory: normalData.Path);
        using (HttpClient client = normalFactory.CreateClient())
        {
            Assert.Equal(
                System.Net.HttpStatusCode.OK,
                (await client.GetAsync("/Login")).StatusCode);
        }

        await AssertHostLockUnavailableAsync(normalData.Path);
        await normalFactory.DisposeAsync();
        await AssertHostLockAvailableAsync(normalData.Path);

        using TestDataDirectory failureData = new();
        HostBlockingLifecycleService? failingService = null;
        CreatorToolkitWebFactory failureFactory = new(
            services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IHostedService>(
                    provider =>
                    {
                        failingService = new HostBlockingLifecycleService(
                            provider.GetRequiredService<ApplicationLifecycleCoordinator>(),
                            provider.GetRequiredService<ApplicationLifecycleOptions>(),
                            provider.GetRequiredService<TimeProvider>(),
                            provider.GetRequiredService<IHostApplicationLifetime>(),
                            provider.GetRequiredService<
                                ILogger<ApplicationLifecycleHostedService>>());
                        return failingService;
                    });
            },
            failureData.Path);
        using (HttpClient client = failureFactory.CreateClient())
        {
            Assert.Equal(
                System.Net.HttpStatusCode.OK,
                (await client.GetAsync("/Login")).StatusCode);
        }

        Assert.NotNull(failingService);
        failingService.CompleteLate(fail: true);
        await failureFactory.DisposeAsync();
        Assert.Equal(
            ApplicationLifecycleState.Failed,
            failingService.Coordinator.GetStatus().State);
        await AssertHostLockAvailableAsync(failureData.Path);

        using TestDataDirectory timeoutData = new();
        HostBlockingLifecycleService? blockingService = null;
        CreatorToolkitWebFactory timeoutFactory = new(
            services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ApplicationLifecycleOptions>();
                services.AddSingleton(
                    new ApplicationLifecycleOptions(TimeSpan.FromMilliseconds(75)));
                services.AddSingleton<IHostedService>(
                    provider =>
                    {
                        blockingService = new HostBlockingLifecycleService(
                            provider.GetRequiredService<ApplicationLifecycleCoordinator>(),
                            provider.GetRequiredService<ApplicationLifecycleOptions>(),
                            provider.GetRequiredService<TimeProvider>(),
                            provider.GetRequiredService<IHostApplicationLifetime>(),
                            provider.GetRequiredService<
                                ILogger<ApplicationLifecycleHostedService>>());
                        return blockingService;
                    });
            },
            timeoutData.Path);
        using (HttpClient client = timeoutFactory.CreateClient())
        {
            Assert.Equal(
                System.Net.HttpStatusCode.OK,
                (await client.GetAsync("/Login")).StatusCode);
        }

        Assert.NotNull(blockingService);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await timeoutFactory.DisposeAsync();
        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(
            ApplicationLifecycleState.Failed,
            blockingService.Coordinator.GetStatus().State);
        await AssertHostLockAvailableAsync(timeoutData.Path);

        blockingService.CompleteLate(fail: true);
        await blockingService.ShutdownExited.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.Equal(
            ApplicationLifecycleState.Failed,
            blockingService.Coordinator.GetStatus().State);
        Assert.False(blockingService.Coordinator.GetStatus().AcceptingLifecycleWork);
    }

    [Fact]
    public async Task StartupFailuresAfterLockAcquisitionReleaseApplicationLock()
    {
        using TestDataDirectory lifecycleFailureData = new();
        CreatorToolkitWebFactory lifecycleFailureFactory = new(
            services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IHostedService>(
                    provider => new FailingHostedLifecycleService(
                        provider.GetRequiredService<ApplicationLifecycleCoordinator>(),
                        provider.GetRequiredService<ApplicationLifecycleOptions>(),
                        provider.GetRequiredService<TimeProvider>(),
                        provider.GetRequiredService<IHostApplicationLifetime>(),
                        provider.GetRequiredService<
                            ILogger<ApplicationLifecycleHostedService>>()));
            },
            lifecycleFailureData.Path);
        Assert.ThrowsAny<Exception>(() => lifecycleFailureFactory.CreateClient());
        await lifecycleFailureFactory.DisposeAsync();
        await AssertHostLockAvailableAsync(lifecycleFailureData.Path);

        using TestDataDirectory laterFailureData = new();
        CreatorToolkitWebFactory laterFailureFactory = new(
            services => services.AddSingleton<IHostedService, ThrowingStartupHostedService>(),
            laterFailureData.Path);
        Assert.ThrowsAny<Exception>(() => laterFailureFactory.CreateClient());
        await laterFailureFactory.DisposeAsync();
        await AssertHostLockAvailableAsync(laterFailureData.Path);
    }

    private static ControllableLifecycleService CreateService(
        ApplicationLifecycleCoordinator coordinator,
        RecordingLogger logger,
        TimeSpan? timeout = null,
        TestHostApplicationLifetime? lifetime = null) =>
        new(
            coordinator,
            new ApplicationLifecycleOptions(timeout ?? TimeSpan.FromSeconds(1)),
            TimeProvider.System,
            lifetime ?? new TestHostApplicationLifetime(),
            logger);

    private static async Task AssertHostLockUnavailableAsync(string dataDirectory)
    {
        await using ServiceProvider provider = TestServices.Create(dataDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<ApplicationHostLock>().AcquireAsync());
    }

    private static async Task AssertHostLockAvailableAsync(string dataDirectory)
    {
        await using ServiceProvider provider = TestServices.Create(dataDirectory);
        await using ApplicationHostLease lease = await provider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();
    }

    private sealed class ControllableLifecycleService(
        ApplicationLifecycleCoordinator coordinator,
        ApplicationLifecycleOptions options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ApplicationLifecycleHostedService> logger)
        : ApplicationLifecycleHostedService(
            coordinator,
            options,
            timeProvider,
            applicationLifetime,
            logger)
    {
        internal bool BlockStartup { get; set; }

        internal bool BlockShutdown { get; set; }

        internal bool IgnoreShutdownCancellation { get; set; }

        internal bool FailAfterShutdownRelease { get; set; }

        internal Exception? StartupFailure { get; set; }

        internal int ShutdownCallCount { get; private set; }

        internal TaskCompletionSource StartupEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowStartup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StartupExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ShutdownEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowShutdown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ShutdownExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task CompleteStartupAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                StartupEntered.TrySetResult();
                if (StartupFailure is not null)
                {
                    throw StartupFailure;
                }

                if (BlockStartup)
                {
                    await AllowStartup.Task;
                }
            }
            finally
            {
                StartupExited.TrySetResult();
            }
        }

        protected override async Task CompleteShutdownAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                ShutdownCallCount++;
                ShutdownEntered.TrySetResult();
                if (BlockShutdown)
                {
                    if (IgnoreShutdownCancellation)
                    {
                        await AllowShutdown.Task;
                    }
                    else
                    {
                        await AllowShutdown.Task.WaitAsync(cancellationToken);
                    }
                }

                if (FailAfterShutdownRelease)
                {
                    throw new InvalidOperationException("late-shutdown-secret-marker");
                }
            }
            finally
            {
                ShutdownExited.TrySetResult();
            }
        }
    }

    private sealed class HostBlockingLifecycleService(
        ApplicationLifecycleCoordinator coordinator,
        ApplicationLifecycleOptions options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ApplicationLifecycleHostedService> logger)
        : ApplicationLifecycleHostedService(
            coordinator,
            options,
            timeProvider,
            applicationLifetime,
            logger)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _fail;

        internal ApplicationLifecycleCoordinator Coordinator { get; } = coordinator;

        internal TaskCompletionSource ShutdownExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CompleteLate(bool fail)
        {
            _fail = fail;
            _completion.TrySetResult();
        }

        protected override async Task CompleteShutdownAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await _completion.Task;
                if (_fail)
                {
                    throw new InvalidOperationException("late-shutdown-secret-marker");
                }
            }
            finally
            {
                ShutdownExited.TrySetResult();
            }
        }
    }

    private sealed class FailingHostedLifecycleService(
        ApplicationLifecycleCoordinator coordinator,
        ApplicationLifecycleOptions options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ApplicationLifecycleHostedService> logger)
        : ApplicationLifecycleHostedService(
            coordinator,
            options,
            timeProvider,
            applicationLifetime,
            logger)
    {
        protected override Task CompleteStartupAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("host-startup-secret-marker");
    }

    private sealed class SynchronousShutdownFailureLifecycleService(
        ApplicationLifecycleCoordinator coordinator,
        ApplicationLifecycleOptions options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ApplicationLifecycleHostedService> logger)
        : ApplicationLifecycleHostedService(
            coordinator,
            options,
            timeProvider,
            applicationLifetime,
            logger)
    {
        protected override Task CompleteShutdownAsync(CancellationToken cancellationToken) =>
            throw new IOException("database-path-marker");
    }

    private sealed class ThrowingStartupHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("later-startup-marker"));

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger<ApplicationLifecycleHostedService>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
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

        public void StopApplication()
        {
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
