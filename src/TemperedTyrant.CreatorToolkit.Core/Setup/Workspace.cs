namespace TemperedTyrant.CreatorToolkit.Core.Setup;

public sealed class Workspace
{
    public const int SingletonId = 1;

    private Workspace()
    {
    }

    public int Id { get; private set; } = SingletonId;

    public string TimeZoneId { get; private set; } = "Etc/UTC";

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public long Revision { get; private set; }
}
