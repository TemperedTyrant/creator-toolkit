namespace TemperedTyrant.CreatorToolkit.Core.Audit;

public sealed class AuditRecord
{
    private AuditRecord()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventCode { get; private set; } = string.Empty;

    public Guid? ActorUserId { get; private set; }

    public Guid? TargetUserId { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public string? ReasonCode { get; private set; }

    public string? DiagnosticReference { get; private set; }
}
