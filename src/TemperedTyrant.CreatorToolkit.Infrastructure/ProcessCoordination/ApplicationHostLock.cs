using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

public sealed class ApplicationHostLock
{
    private readonly FileProcessLock _processLock;

    public ApplicationHostLock(DataDirectoryLayoutProvider layoutProvider, TimeProvider timeProvider)
    {
        _processLock = new FileProcessLock(
            Path.Combine(layoutProvider.Layout.LockPath, "application-host.lock"),
            timeProvider);
    }

    public async Task<ApplicationHostLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            FileProcessLockLease lease = await _processLock.AcquireAsync(TimeSpan.Zero, cancellationToken);
            return new ApplicationHostLease(lease);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "Another application host is already using the configured data directory.");
        }
    }

    public bool IsHeld()
    {
        return _processLock.IsHeld();
    }
}

public sealed class ApplicationHostLease : IAsyncDisposable
{
    private readonly FileProcessLockLease _lease;

    internal ApplicationHostLease(FileProcessLockLease lease)
    {
        _lease = lease;
    }

    public ValueTask DisposeAsync()
    {
        return _lease.DisposeAsync();
    }
}
