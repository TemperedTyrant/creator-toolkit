using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

public sealed class SecurityOperationCoordinator
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);
    private readonly FileProcessLock _processLock;

    public SecurityOperationCoordinator(
        DataDirectoryLayoutProvider layoutProvider,
        TimeProvider timeProvider)
    {
        _processLock = new FileProcessLock(
            Path.Combine(layoutProvider.Layout.LockPath, "security-operation.lock"),
            timeProvider);
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using FileProcessLockLease lease =
            await _processLock.AcquireAsync(AcquisitionTimeout, cancellationToken);
        await operation(cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using FileProcessLockLease lease =
            await _processLock.AcquireAsync(AcquisitionTimeout, cancellationToken);
        return await operation(cancellationToken);
    }
}
