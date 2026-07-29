namespace TemperedTyrant.CreatorToolkit.Core.Audit;

public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
