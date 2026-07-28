namespace TemperedTyrant.CreatorToolkit.Core.Setup;

public sealed class SecurityCapability
{
    private SecurityCapability()
    {
    }

    public Guid Id { get; private set; }

    public CapabilityPurpose Purpose { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public Guid? SubjectUserId { get; private set; }

    public string? ActiveSlot { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public long Revision { get; private set; }
}
