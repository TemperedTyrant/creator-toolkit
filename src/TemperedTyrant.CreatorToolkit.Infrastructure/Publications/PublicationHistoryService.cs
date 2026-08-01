using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Publications;

internal sealed class PublicationHistoryService(
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IPublicationHistoryService
{
    public async Task<PublicationHistoryPage> ListAsync(
        PublicationHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        int pageSize = Math.Clamp(request.PageSize, 1, PublicationHistoryPage.MaximumPageSize);
        IQueryable<Publication> query = dbContext.Publications.AsNoTracking();
        if (request.Status is not null)
        {
            query = query.Where(value => value.Status == request.Status);
        }

        if (request.Provider is not null)
        {
            query = query.Where(value => value.Provider == request.Provider);
        }

        if (request.RequestedFromUtc is not null)
        {
            query = query.Where(value => value.RequestedAtUtc >= request.RequestedFromUtc.Value);
        }

        if (request.RequestedToUtc is not null)
        {
            query = query.Where(value => value.RequestedAtUtc <= request.RequestedToUtc.Value);
        }

        int totalItems = await query.CountAsync(cancellationToken);
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
        int page = Math.Clamp(request.Page, 1, totalPages);
        PublicationHistoryItem[] items = await query
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ThenByDescending(value => value.RequestedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(value => new PublicationHistoryItem(
                value.Id,
                value.AnnouncementId,
                value.AnnouncementRevision,
                value.Provider,
                value.RequestedAtUtc,
                value.Status,
                value.TotalDeliveryCount,
                value.SuccessfulDeliveryCount,
                value.FailedDeliveryCount,
                value.CancelledDeliveryCount,
                value.Revision))
            .ToArrayAsync(cancellationToken);
        return new PublicationHistoryPage(items, page, pageSize, totalItems, totalPages);
    }

    public async Task<PublicationHistoryDetails?> GetAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Publication? publication = await dbContext.Publications
            .AsNoTracking()
            .Include(value => value.Deliveries)
            .ThenInclude(value => value.Attempts)
            .SingleOrDefaultAsync(value => value.Id == publicationId, cancellationToken);
        if (publication is null)
        {
            return null;
        }

        return new PublicationHistoryDetails(
            Item(publication),
            publication.CancellationRequestedAtUtc,
            publication.RequestedByUserId,
            publication.Deliveries
                .OrderBy(value => value.ServerNameSnapshot)
                .ThenBy(value => value.ChannelNameSnapshot)
                .Select(value => new PublicationDeliveryDetails(
                    value.Id,
                    value.LocalDestinationId,
                    value.ServerNameSnapshot,
                    value.ChannelNameSnapshot,
                    value.Status,
                    value.AttemptCount,
                    value.Status == PublicationDeliveryStatus.RetryScheduled
                        ? value.NextAttemptAtUtc
                        : null,
                    value.LastSafeOutcome,
                    value.ExternalMessageId,
                    value.StartedAtUtc,
                    value.CompletedAtUtc,
                    value.Attempts.OrderBy(attempt => attempt.AttemptNumber)
                        .Select(attempt => new PublicationAttemptDetails(
                            attempt.AttemptNumber,
                            attempt.StartedAtUtc,
                            attempt.CompletedAtUtc,
                            attempt.SafeOutcome,
                            attempt.RetryScheduledForUtc,
                            attempt.ExternalMessageId,
                            attempt.DiagnosticReference))
                        .ToArray()))
                .ToArray());
    }

    public async Task<PublicationCancellationResult> CancelAsync(
        Guid publicationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Publication? publication = await dbContext.Publications
            .Include(value => value.Deliveries)
            .SingleOrDefaultAsync(value => value.Id == publicationId, cancellationToken);
        if (publication is null)
        {
            return PublicationCancellationResult.NotFound;
        }

        PublicationMutationResult mutation = publication.RequestCancellation(
            expectedRevision,
            timeProvider.GetUtcNow());
        if (mutation == PublicationMutationResult.StaleRevision)
        {
            return PublicationCancellationResult.StaleRevision;
        }

        if (mutation == PublicationMutationResult.InvalidTransition)
        {
            return PublicationCancellationResult.AlreadyRequestedOrTerminal;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (PublicationDelivery delivery in publication.Deliveries)
        {
            if (delivery.CancelPending(now))
            {
                await auditWriter.WriteAsync(
                    new AuditEvent(
                        AuditEventCode.PublicationDeliveryCancelled,
                        AuditOutcome.Succeeded,
                        actorUserId),
                    cancellationToken);
            }
        }

        publication.Recalculate(publication.Deliveries.ToArray(), now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.PublicationCancellationRequested,
                AuditOutcome.Succeeded,
                actorUserId),
            cancellationToken);
        if (Publication.IsTerminal(publication.Status))
        {
            await RemovePayloadAsync(publication.Id, actorUserId, cancellationToken);
            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.PublicationFinalized,
                    AuditOutcome.Succeeded,
                    actorUserId),
                cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PublicationCancellationResult.Succeeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return PublicationCancellationResult.StaleRevision;
        }
    }

    private async Task RemovePayloadAsync(
        Guid publicationId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        PublicationPayload? payload = await dbContext.PublicationPayloads
            .SingleOrDefaultAsync(value => value.PublicationId == publicationId, cancellationToken);
        if (payload is null)
        {
            return;
        }

        dbContext.PublicationPayloads.Remove(payload);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.PublicationPayloadRemoved,
                AuditOutcome.Succeeded,
                actorUserId),
            cancellationToken);
    }

    private static PublicationHistoryItem Item(Publication value) => new(
        value.Id,
        value.AnnouncementId,
        value.AnnouncementRevision,
        value.Provider,
        value.RequestedAtUtc,
        value.Status,
        value.TotalDeliveryCount,
        value.SuccessfulDeliveryCount,
        value.FailedDeliveryCount,
        value.CancelledDeliveryCount,
        value.Revision);
}
