namespace TemperedTyrant.CreatorToolkit.Core.Diagnostics;

public interface IDiagnosticRecorder
{
    Task RecordAsync(DiagnosticRecord record, CancellationToken cancellationToken = default);
}
