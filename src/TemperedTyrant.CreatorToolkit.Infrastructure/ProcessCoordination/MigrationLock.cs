using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

public sealed class MigrationLock
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);
    private readonly FileProcessLock _processLock;

    public MigrationLock(DataDirectoryLayoutProvider layoutProvider, TimeProvider timeProvider)
    {
        _processLock = new FileProcessLock(
            Path.Combine(layoutProvider.Layout.LockPath, "migration.lock"),
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
}
