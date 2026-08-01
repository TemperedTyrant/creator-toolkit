using TemperedTyrant.CreatorToolkit.Core.Publications;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Publications;

internal sealed class PublicationPayload
{
    public Guid PublicationId { get; set; }

    public Publication Publication { get; set; } = null!;

    public byte[] Ciphertext { get; set; } = [];

    public int PlaintextSize { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
