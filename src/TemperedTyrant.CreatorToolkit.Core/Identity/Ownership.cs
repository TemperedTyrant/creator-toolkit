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

    public static Ownership Create(
        int workspaceId,
        Guid ownerUserId,
        DateTimeOffset transferredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workspaceId);

        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("An owner user identifier is required.", nameof(ownerUserId));
        }

        return new Ownership
        {
            WorkspaceId = workspaceId,
            OwnerUserId = ownerUserId,
            TransferredAtUtc = transferredAtUtc,
        };
    }
}
