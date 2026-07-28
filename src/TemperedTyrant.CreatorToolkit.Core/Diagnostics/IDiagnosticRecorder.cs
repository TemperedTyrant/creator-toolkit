namespace TemperedTyrant.CreatorToolkit.Core.Diagnostics;

public interface IDiagnosticRecorder
{
    Task<DiagnosticReference> RecordAsync(
        UnexpectedDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default);
}
