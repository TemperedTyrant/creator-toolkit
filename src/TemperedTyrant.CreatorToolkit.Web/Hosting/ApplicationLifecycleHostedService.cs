using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TemperedTyrant.CreatorToolkit.Web.Hosting;

public partial class ApplicationLifecycleHostedService(
    ApplicationLifecycleCoordinator coordinator,
    ApplicationLifecycleOptions options,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ApplicationLifecycleHostedService> logger,
    ApplicationHostLockLifetime? applicationHostLockLifetime = null) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _stoppingRegistration;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        long generation = coordinator.BeginStartup();
        _stoppingRegistration = applicationLifetime.ApplicationStopping.Register(
            static state => ((ApplicationLifecycleCoordinator)state!).SignalStopping(),
            coordinator);
        LogStarting(logger);
        try
        {
            Task startupTask = CompleteStartupAsync(cancellationToken);
            ObserveLateFailure(startupTask);
            await startupTask.WaitAsync(cancellationToken);
            if (!coordinator.TryMarkRunning(generation))
            {
                throw new OperationCanceledException(
                    "Application lifecycle startup was interrupted.",
                    cancellationToken);
            }

            LogRunning(logger);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            coordinator.MarkFailed(generation);
            LogStartupFailed(logger);
            _stoppingRegistration.Dispose();
            throw;
        }
        catch (Exception)
        {
            coordinator.MarkFailed(generation);
            LogStartupFailed(logger);
            _stoppingRegistration.Dispose();
            throw new InvalidOperationException("Application lifecycle startup failed.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        coordinator.SignalStopping();
        if (!coordinator.TryClaimShutdown(out long generation))
        {
            return;
        }

        LogStopping(logger);
        using CancellationTokenSource timeoutSource =
            new(options.ShutdownTimeout, timeProvider);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        try
        {
            Task shutdownTask = CompleteShutdownAsync(linkedSource.Token);
            ObserveLateFailure(shutdownTask);
            await shutdownTask.WaitAsync(linkedSource.Token);
            if (applicationHostLockLifetime is not null)
            {
                await applicationHostLockLifetime.ReleaseAsync();
            }

            if (coordinator.TryMarkStopped(generation))
            {
                LogStopped(logger);
            }
        }
        catch (OperationCanceledException)
        {
            coordinator.MarkFailed(generation);
            if (timeoutSource.IsCancellationRequested)
            {
                LogShutdownTimedOut(logger);
            }
            else
            {
                LogShutdownCancelled(logger);
            }
        }
        catch (Exception)
        {
            coordinator.MarkFailed(generation);
            LogShutdownFailed(logger);
        }
        finally
        {
            _stoppingRegistration.Dispose();
        }
    }

    public void Dispose()
    {
        _stoppingRegistration.Dispose();
        GC.SuppressFinalize(this);
    }

    protected virtual Task CompleteStartupAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected virtual Task CompleteShutdownAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Information,
        Message = "Application lifecycle is starting.")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Information,
        Message = "Application lifecycle is running.")]
    private static partial void LogRunning(ILogger logger);

    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Information,
        Message = "Application lifecycle is stopping.")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(
        EventId = 7004,
        Level = LogLevel.Information,
        Message = "Application lifecycle is stopped.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(
        EventId = 7005,
        Level = LogLevel.Critical,
        Message = "Application lifecycle startup failed.")]
    private static partial void LogStartupFailed(ILogger logger);

    [LoggerMessage(
        EventId = 7006,
        Level = LogLevel.Warning,
        Message = "Application lifecycle shutdown timed out.")]
    private static partial void LogShutdownTimedOut(ILogger logger);

    [LoggerMessage(
        EventId = 7007,
        Level = LogLevel.Warning,
        Message = "Application lifecycle shutdown was cancelled.")]
    private static partial void LogShutdownCancelled(ILogger logger);

    [LoggerMessage(
        EventId = 7008,
        Level = LogLevel.Error,
        Message = "Application lifecycle shutdown failed.")]
    private static partial void LogShutdownFailed(ILogger logger);
}

public sealed record ApplicationLifecycleOptions(TimeSpan ShutdownTimeout)
{
    public static ApplicationLifecycleOptions Default { get; } =
        new(TimeSpan.FromSeconds(10));
}
