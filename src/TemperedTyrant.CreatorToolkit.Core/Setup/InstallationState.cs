namespace TemperedTyrant.CreatorToolkit.Core.Setup;

public sealed class InstallationState
{
    public const int SingletonId = 1;

    private InstallationState()
    {
    }

    public int Id { get; private set; } = SingletonId;

    public DateTimeOffset? InitializedAtUtc { get; private set; }

    public long Revision { get; private set; }

    public void MarkInitialized(DateTimeOffset initializedAtUtc)
    {
        if (InitializedAtUtc is not null)
        {
            throw new InvalidOperationException("The installation is already initialized.");
        }

        InitializedAtUtc = initializedAtUtc;
    }
}
