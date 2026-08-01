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

    public static DiagnosticRecord Create(
        DiagnosticReference reference,
        UnexpectedDiagnosticEvent diagnosticEvent,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        (string category, string errorCode) = diagnosticEvent.FailureKind switch
        {
            DiagnosticFailureKind.UnhandledRequest => ("internal", "unhandled-request"),
            DiagnosticFailureKind.Infrastructure => ("infrastructure", "infrastructure-failure"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(diagnosticEvent),
                "The diagnostic failure kind is not supported."),
        };

        string operation = diagnosticEvent.Operation switch
        {
            DiagnosticOperation.HttpRequest => "http-request",
            DiagnosticOperation.PersistenceInitialization => "persistence-initialization",
            DiagnosticOperation.DiscordServerDiscovery => "discord-server-discovery",
            _ => throw new ArgumentOutOfRangeException(
                nameof(diagnosticEvent),
                "The diagnostic operation is not supported."),
        };

        return new DiagnosticRecord
        {
            Id = Guid.NewGuid(),
            Reference = reference.Value,
            OccurredAtUtc = occurredAtUtc,
            Severity = "error",
            Category = category,
            ErrorCode = errorCode,
            Operation = operation,
            ExceptionType = diagnosticEvent.ExceptionTypeCode,
        };
    }
}
