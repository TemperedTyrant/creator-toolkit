namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

internal sealed class ProtectedSecretRecord
{
    public Guid Id { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public string Ciphertext { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public long Revision { get; set; }
}
