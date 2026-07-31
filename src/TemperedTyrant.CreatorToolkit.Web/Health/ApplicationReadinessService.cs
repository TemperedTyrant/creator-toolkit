using Microsoft.Extensions.Hosting;
using TemperedTyrant.CreatorToolkit.Infrastructure.Health;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.Hosting;

namespace TemperedTyrant.CreatorToolkit.Web.Health;

public sealed class ApplicationReadinessService
{
    private readonly object _sync = new();
    private readonly IInfrastructureReadinessProbe _infrastructureProbe;
    private readonly ApplicationLifecycleCoordinator _lifecycleCoordinator;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly HealthReadinessOptions _options;
    private readonly TimeProvider _timeProvider;
    private ProbeOperation? _inFlightOperation;

    public ApplicationReadinessService(
        CreatorToolkitOptions validatedOptions,
        IInfrastructureReadinessProbe infrastructureProbe,
        ApplicationLifecycleCoordinator lifecycleCoordinator,
        IHostApplicationLifetime applicationLifetime,
        HealthReadinessOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(validatedOptions);
        _infrastructureProbe = infrastructureProbe;
        _lifecycleCoordinator = lifecycleCoordinator;
        _applicationLifetime = applicationLifetime;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<bool> IsReadyAsync(CancellationToken requestCancellationToken)
    {
        requestCancellationToken.ThrowIfCancellationRequested();
        if (!HasRunningLifecycle())
        {
            return false;
        }

        Task<bool> boundedResult = GetOrStartProbe();
        bool probeReady = await boundedResult.WaitAsync(requestCancellationToken);
        return probeReady && HasRunningLifecycle();
    }

    private Task<bool> GetOrStartProbe()
    {
        lock (_sync)
        {
            if (_inFlightOperation is not null)
            {
                if (!_inFlightOperation.ProbeTask.IsCompleted)
                {
                    return _inFlightOperation.BoundedResult;
                }

                _inFlightOperation.Dispose();
                _inFlightOperation = null;
            }

            CancellationTokenSource timeoutSource =
                new(_options.OverallTimeout, _timeProvider);
            Task<bool> probeTask = Task.Run(
                () => RunInfrastructureProbeAsync(timeoutSource.Token),
                CancellationToken.None);
            ProbeOperation operation = new(timeoutSource, probeTask);
            operation.BoundedResult = AwaitBoundedResultAsync(operation);
            _inFlightOperation = operation;
            _ = probeTask.ContinueWith(
                static (completed, state) =>
                {
                    var completion = ((ApplicationReadinessService Service,
                        ProbeOperation Operation))state!;
                    completion.Service.ClearCompletedProbe(
                        completion.Operation,
                        completed);
                },
                (this, operation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return operation.BoundedResult;
        }
    }

    private async Task<bool> RunInfrastructureProbeAsync(
        CancellationToken timeoutToken)
    {
        try
        {
            return await _infrastructureProbe.IsReadyAsync(timeoutToken);
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> AwaitBoundedResultAsync(ProbeOperation operation)
    {
        try
        {
            bool infrastructureReady = await operation.ProbeTask.WaitAsync(
                operation.TimeoutSource.Token);
            return infrastructureReady && HasRunningLifecycle();
        }
        catch (OperationCanceledException)
            when (operation.TimeoutSource.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool HasRunningLifecycle()
    {
        return !_applicationLifetime.ApplicationStopping.IsCancellationRequested
            && _lifecycleCoordinator.GetStatus().State == ApplicationLifecycleState.Running;
    }

    private void ClearCompletedProbe(
        ProbeOperation operation,
        Task<bool> completedProbe)
    {
        _ = completedProbe.Exception;
        lock (_sync)
        {
            if (ReferenceEquals(_inFlightOperation, operation))
            {
                _inFlightOperation = null;
            }
        }

        operation.Dispose();
    }

    private sealed class ProbeOperation(
        CancellationTokenSource timeoutSource,
        Task<bool> probeTask)
    {
        private int _disposed;

        internal CancellationTokenSource TimeoutSource { get; } = timeoutSource;

        internal Task<bool> ProbeTask { get; } = probeTask;

        internal Task<bool> BoundedResult { get; set; } = null!;

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                TimeoutSource.Dispose();
            }
        }
    }
}

public sealed record HealthReadinessOptions(TimeSpan OverallTimeout)
{
    public static HealthReadinessOptions Default { get; } =
        new(TimeSpan.FromSeconds(2));
}
