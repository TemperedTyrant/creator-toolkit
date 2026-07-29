using Microsoft.Extensions.Hosting;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Web.Hosting;

public sealed class ApplicationHostLockLifetime(
    ApplicationHostLock applicationHostLock) : IHostedLifecycleService, IAsyncDisposable
{
    private readonly object _sync = new();
    private ApplicationHostLease? _lease;
    private bool _acquiring;
    private bool _closed;

    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_closed || _acquiring || _lease is not null)
            {
                throw new InvalidOperationException(
                    "Application host lock acquisition is not valid in the current state.");
            }

            _acquiring = true;
        }

        ApplicationHostLease? lease = null;
        try
        {
            lease = await applicationHostLock.AcquireAsync(cancellationToken);
            lock (_sync)
            {
                _acquiring = false;
                if (!_closed)
                {
                    _lease = lease;
                    lease = null;
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                _acquiring = false;
            }

            throw;
        }

        if (lease is not null)
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException(
                "Application host lock acquisition is not valid in the current state.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_closed || _lease is null)
            {
                throw new InvalidOperationException("Application host lock is not held.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        await ReleaseAsync();
    }

    public ValueTask DisposeAsync()
    {
        return ReleaseAsync();
    }

    private async ValueTask ReleaseAsync()
    {
        ApplicationHostLease? lease;
        lock (_sync)
        {
            _closed = true;
            lease = _lease;
            _lease = null;
        }

        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }
}
