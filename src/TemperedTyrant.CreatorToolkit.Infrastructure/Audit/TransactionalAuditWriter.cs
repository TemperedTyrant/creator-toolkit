using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Audit;

internal sealed class TransactionalAuditWriter(
    CreatorToolkitDbContext dbContext,
    TimeProvider timeProvider) : IAuditWriter
{
    public Task WriteAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AuditRecord record = AuditRecord.Create(
            auditEvent,
            timeProvider.GetUtcNow().ToUniversalTime());
        dbContext.AuditRecords.Add(record);

        // The protected operation owns SaveChanges and the surrounding transaction.
        return Task.CompletedTask;
    }
}
