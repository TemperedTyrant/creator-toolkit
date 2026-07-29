namespace TemperedTyrant.CreatorToolkit.Web.Hosting;

public sealed class ApplicationLifecycleCoordinator
{
    private readonly object _sync = new();
    private readonly long _generation = 1;
    private ApplicationLifecycleState _state = ApplicationLifecycleState.Starting;
    private bool _shutdownClaimed;

    public ApplicationLifecycleStatus GetStatus()
    {
        lock (_sync)
        {
            return CreateStatus(_state);
        }
    }

    internal long BeginStartup()
    {
        lock (_sync)
        {
            if (_state != ApplicationLifecycleState.Starting)
            {
                throw new InvalidOperationException(
                    "Application lifecycle startup is not valid in the current state.");
            }

            return _generation;
        }
    }

    internal bool TryMarkRunning(long generation)
    {
        lock (_sync)
        {
            if (generation != _generation
                || _state != ApplicationLifecycleState.Starting)
            {
                return false;
            }

            _state = ApplicationLifecycleState.Running;
            return true;
        }
    }

    internal void SignalStopping()
    {
        lock (_sync)
        {
            if (_state is ApplicationLifecycleState.Starting
                or ApplicationLifecycleState.Running)
            {
                _state = ApplicationLifecycleState.Stopping;
            }
        }
    }

    internal bool TryClaimShutdown(out long generation)
    {
        lock (_sync)
        {
            generation = _generation;
            if (_state is ApplicationLifecycleState.Stopped
                or ApplicationLifecycleState.Failed
                || _shutdownClaimed)
            {
                return false;
            }

            if (_state is ApplicationLifecycleState.Starting
                or ApplicationLifecycleState.Running)
            {
                _state = ApplicationLifecycleState.Stopping;
            }

            if (_state != ApplicationLifecycleState.Stopping)
            {
                return false;
            }

            _shutdownClaimed = true;
            return true;
        }
    }

    internal bool TryMarkStopped(long generation)
    {
        lock (_sync)
        {
            if (generation != _generation
                || _state != ApplicationLifecycleState.Stopping
                || !_shutdownClaimed)
            {
                return false;
            }

            _state = ApplicationLifecycleState.Stopped;
            return true;
        }
    }

    internal void MarkFailed(long generation)
    {
        lock (_sync)
        {
            if (generation == _generation
                && _state != ApplicationLifecycleState.Stopped)
            {
                _state = ApplicationLifecycleState.Failed;
            }
        }
    }

    private static ApplicationLifecycleStatus CreateStatus(
        ApplicationLifecycleState state) =>
        new(state, AcceptingLifecycleWork: state == ApplicationLifecycleState.Running);
}

public enum ApplicationLifecycleState
{
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Failed = 5,
}

public sealed record ApplicationLifecycleStatus(
    ApplicationLifecycleState State,
    bool AcceptingLifecycleWork);
