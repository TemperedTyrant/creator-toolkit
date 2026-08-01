namespace TemperedTyrant.CreatorToolkit.Core.Diagnostics;

public sealed record UnexpectedDiagnosticEvent(
    DiagnosticFailureKind FailureKind,
    DiagnosticOperation Operation,
    DiagnosticExceptionType ExceptionType = DiagnosticExceptionType.Unexpected)
{
    public bool HasSpecificDeduplicationKey =>
        FailureKind == DiagnosticFailureKind.Infrastructure
        && Operation == DiagnosticOperation.PersistenceInitialization;

    public string ExceptionTypeCode => ExceptionType switch
    {
        DiagnosticExceptionType.Unexpected => "unexpected",
        DiagnosticExceptionType.InvalidOperation => "invalid-operation",
        DiagnosticExceptionType.InputOutput => "input-output",
        DiagnosticExceptionType.Timeout => "timeout",
        DiagnosticExceptionType.Cryptography => "cryptography",
        DiagnosticExceptionType.Database => "database",
        _ => throw new ArgumentOutOfRangeException(
            nameof(ExceptionType),
            "The diagnostic exception type is not supported."),
    };
}

public enum DiagnosticFailureKind
{
    UnhandledRequest = 1,
    Infrastructure = 2,
}

public enum DiagnosticOperation
{
    HttpRequest = 1,
    PersistenceInitialization = 2,
    DiscordServerDiscovery = 3,
    DiscordPublicationProcessing = 4,
}

public enum DiagnosticExceptionType
{
    Unexpected = 1,
    InvalidOperation = 2,
    InputOutput = 3,
    Timeout = 4,
    Cryptography = 5,
    Database = 6,
}
