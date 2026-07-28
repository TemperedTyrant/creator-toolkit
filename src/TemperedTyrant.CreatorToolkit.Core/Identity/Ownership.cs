namespace TemperedTyrant.CreatorToolkit.Core.Identity;

public sealed class Ownership
{
    private Ownership()
    {
    }

    public int WorkspaceId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public DateTimeOffset TransferredAtUtc { get; private set; }

    public long Revision { get; private set; }
}
