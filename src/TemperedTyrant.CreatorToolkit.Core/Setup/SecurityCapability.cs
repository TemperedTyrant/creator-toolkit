namespace TemperedTyrant.CreatorToolkit.Core.Setup;

public sealed class SecurityCapability
{
    public const string BootstrapOwnerActiveSlot = "bootstrap-owner";

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

    public static SecurityCapability CreateBootstrapOwner(
        byte[] tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
        {
            throw new ArgumentException("A capability hash must contain 32 bytes.", nameof(tokenHash));
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "A capability must expire after it is created.");
        }

        return new SecurityCapability
        {
            Id = Guid.NewGuid(),
            Purpose = CapabilityPurpose.BootstrapOwner,
            TokenHash = [.. tokenHash],
            ActiveSlot = BootstrapOwnerActiveSlot,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
        };
    }

    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        if (UsedAtUtc is not null || RevokedAtUtc is not null)
        {
            throw new InvalidOperationException("The capability is already terminal.");
        }

        if (revokedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revokedAtUtc),
                "A capability cannot be revoked before it is created.");
        }

        RevokedAtUtc = revokedAtUtc;
        ActiveSlot = null;
    }

    public void Consume(DateTimeOffset usedAtUtc)
    {
        if (UsedAtUtc is not null || RevokedAtUtc is not null)
        {
            throw new InvalidOperationException("The capability is already terminal.");
        }

        if (usedAtUtc < CreatedAtUtc || usedAtUtc >= ExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usedAtUtc),
                "A capability can be consumed only during its validity window.");
        }

        UsedAtUtc = usedAtUtc;
        ActiveSlot = null;
    }
}
