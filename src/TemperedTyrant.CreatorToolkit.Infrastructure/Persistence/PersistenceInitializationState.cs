namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed class PersistenceInitializationState
{
    private readonly object _sync = new();
    private PersistenceInitializationStatus _status =
        PersistenceInitializationStatus.NotStarted;

    public PersistenceInitializationStatus GetStatus()
    {
        lock (_sync)
        {
            return _status;
        }
    }

    internal void MarkRunning()
    {
        lock (_sync)
        {
            _status = PersistenceInitializationStatus.Running;
        }
    }

    internal void MarkSucceeded()
    {
        lock (_sync)
        {
            _status = PersistenceInitializationStatus.Succeeded;
        }
    }

    internal void MarkFailed()
    {
        lock (_sync)
        {
            _status = PersistenceInitializationStatus.Failed;
        }
    }
}

public enum PersistenceInitializationStatus
{
    NotStarted = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}
