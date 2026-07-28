namespace TemperedTyrant.CreatorToolkit.Core.Diagnostics;

public sealed class DiagnosticRecord
{
    private DiagnosticRecord()
    {
    }

    public Guid Id { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string Severity { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    public string ErrorCode { get; private set; } = string.Empty;

    public string Operation { get; private set; } = string.Empty;

    public string? ExceptionType { get; private set; }
}
