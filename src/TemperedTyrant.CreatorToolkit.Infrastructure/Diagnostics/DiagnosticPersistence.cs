using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;

internal sealed class DiagnosticPersistence(
    CreatorToolkitDbContext dbContext,
    TimeProvider timeProvider)
{
    internal async Task<DiagnosticReference> PersistAsync(
        DiagnosticReference candidateReference,
        UnexpectedDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        DateTimeOffset oldestRetained = now - DiagnosticRetention.MaximumAge;
        DateTimeOffset duplicateCutoff = now - DiagnosticRetention.DuplicateWindow;

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM DiagnosticRecords
            WHERE Id IN (
                SELECT Id
                FROM DiagnosticRecords
                WHERE julianday(OccurredAtUtc) < julianday({oldestRetained})
                ORDER BY julianday(OccurredAtUtc), Id
                LIMIT {DiagnosticRetention.MaximumRecords}
            );
            """, cancellationToken);

        DiagnosticRecord comparisonRecord =
            DiagnosticRecord.Create(candidateReference, diagnosticEvent, now);
        DiagnosticRecord? duplicate = null;
        if (diagnosticEvent.HasSpecificDeduplicationKey)
        {
            duplicate = await dbContext.DiagnosticRecords
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM DiagnosticRecords
                    WHERE julianday(OccurredAtUtc) >= julianday({duplicateCutoff})
                        AND Category = {comparisonRecord.Category}
                        AND ErrorCode = {comparisonRecord.ErrorCode}
                        AND Operation = {comparisonRecord.Operation}
                        AND ExceptionType = {comparisonRecord.ExceptionType}
                    ORDER BY julianday(OccurredAtUtc) DESC, Id DESC
                    LIMIT 1
                    """)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new DiagnosticReference(duplicate.Reference);
        }

        bool referenceAlreadyExists = await dbContext.DiagnosticRecords
            .AsNoTracking()
            .AnyAsync(
                record => record.Reference == candidateReference.Value,
                cancellationToken);
        if (referenceAlreadyExists)
        {
            throw new DiagnosticReferenceCollisionException();
        }

        int currentCount = await dbContext.DiagnosticRecords.CountAsync(cancellationToken);
        int numberToRemove = Math.Max(
            0,
            currentCount - (DiagnosticRetention.MaximumRecords - 1));

        if (numberToRemove > 0)
        {
            int boundedNumberToRemove =
                Math.Min(numberToRemove, DiagnosticRetention.MaximumRecords);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM DiagnosticRecords
                WHERE Id IN (
                    SELECT Id
                    FROM DiagnosticRecords
                    ORDER BY julianday(OccurredAtUtc), Id
                    LIMIT {boundedNumberToRemove}
                );
                """, cancellationToken);
        }

        dbContext.DiagnosticRecords.Add(comparisonRecord);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException
            {
                SqliteErrorCode: 19,
                SqliteExtendedErrorCode: 1555 or 2067,
            })
        {
            throw new DiagnosticReferenceCollisionException();
        }
        await transaction.CommitAsync(cancellationToken);

        return candidateReference;
    }
}
