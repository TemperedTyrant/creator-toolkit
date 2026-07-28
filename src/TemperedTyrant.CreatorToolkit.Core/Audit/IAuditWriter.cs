namespace TemperedTyrant.CreatorToolkit.Core.Audit;

public interface IAuditWriter
{
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
