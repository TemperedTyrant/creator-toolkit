using System.Text;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;

internal sealed class AnnouncementService(
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IAnnouncementService
{
    public async Task<AnnouncementPage> ListAsync(
        AnnouncementListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string search = NormalizeSearch(request.Search);
        int pageSize = Math.Clamp(
            request.PageSize,
            1,
            AnnouncementPage.MaximumPageSize);

        IQueryable<Announcement> query = dbContext.Announcements.AsNoTracking();
        query = request.Status switch
        {
            AnnouncementStatusFilter.Draft =>
                query.Where(value => value.Status == AnnouncementStatus.Draft),
            AnnouncementStatusFilter.Archived =>
                query.Where(value => value.Status == AnnouncementStatus.Archived),
            AnnouncementStatusFilter.All => query,
            _ => query.Where(value => value.Status == AnnouncementStatus.Draft),
        };

        if (search.Length > 0)
        {
            string pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(
                value =>
                    EF.Functions.Like(value.Title, pattern, "\\")
                    || EF.Functions.Like(value.Body, pattern, "\\"));
        }

        int totalItems = await query.CountAsync(cancellationToken);
        int totalPages = Math.Max(
            1,
            (int)Math.Ceiling(totalItems / (double)pageSize));
        int page = Math.Clamp(request.Page, 1, totalPages);
        AnnouncementSummary[] items = await query
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ThenBy(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(
                value => new AnnouncementSummary(
                    value.Id,
                    value.Title,
                    value.Status,
                    value.UpdatedAtUtc,
                    value.Revision))
            .ToArrayAsync(cancellationToken);

        return new AnnouncementPage(
            items,
            search,
            request.Status is >= AnnouncementStatusFilter.Draft
                and <= AnnouncementStatusFilter.All
                ? request.Status
                : AnnouncementStatusFilter.Draft,
            page,
            pageSize,
            totalItems,
            totalPages);
    }

    public Task<AnnouncementDetails?> GetAsync(
        Guid announcementId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Announcements
            .AsNoTracking()
            .Where(value => value.Id == announcementId)
            .Select(
                value => new AnnouncementDetails(
                    value.Id,
                    value.Title,
                    value.Body,
                    value.Status,
                    value.CreatedAtUtc,
                    value.UpdatedAtUtc,
                    value.CreatedByUserId,
                    value.UpdatedByUserId,
                    value.Revision))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<AnnouncementOperationResult> CreateAsync(
        Guid announcementId,
        string? title,
        string? body,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        AnnouncementCreationResult creation = Announcement.Create(
            announcementId,
            title,
            body,
            actorUserId,
            timeProvider.GetUtcNow());
        if (!creation.IsSuccess)
        {
            return AnnouncementOperationResult.ValidationFailed(
                announcementId,
                creation.ValidationErrors);
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        long? existingRevision = await dbContext.Announcements
            .AsNoTracking()
            .Where(value => value.Id == announcementId)
            .Select(value => (long?)value.Revision)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingRevision is not null)
        {
            return AnnouncementOperationResult.DuplicateSubmission(
                announcementId,
                existingRevision.Value);
        }

        Announcement announcement = creation.Announcement!;
        dbContext.Announcements.Add(announcement);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.AnnouncementCreated,
                AuditOutcome.Succeeded,
                actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AnnouncementOperationResult.Succeeded(
                announcement.Id,
                announcement.Revision);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            long? duplicateRevision = await dbContext.Announcements
                .AsNoTracking()
                .Where(value => value.Id == announcementId)
                .Select(value => (long?)value.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            if (duplicateRevision is not null)
            {
                return AnnouncementOperationResult.DuplicateSubmission(
                    announcementId,
                    duplicateRevision.Value);
            }

            throw;
        }
    }

    public Task<AnnouncementOperationResult> UpdateAsync(
        Guid announcementId,
        string? title,
        string? body,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            announcementId,
            actorUserId,
            announcement => announcement.Update(
                title,
                body,
                expectedRevision,
                actorUserId,
                timeProvider.GetUtcNow()),
            AuditEventCode.AnnouncementUpdated,
            cancellationToken);
    }

    public Task<AnnouncementOperationResult> ArchiveAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            announcementId,
            actorUserId,
            announcement => announcement.Archive(
                expectedRevision,
                actorUserId,
                timeProvider.GetUtcNow()),
            AuditEventCode.AnnouncementArchived,
            cancellationToken);
    }

    public Task<AnnouncementOperationResult> RestoreAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            announcementId,
            actorUserId,
            announcement => announcement.Restore(
                expectedRevision,
                actorUserId,
                timeProvider.GetUtcNow()),
            AuditEventCode.AnnouncementRestored,
            cancellationToken);
    }

    public async Task<AnnouncementOperationResult> DeleteAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Announcement? announcement = await dbContext.Announcements
            .SingleOrDefaultAsync(value => value.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return AnnouncementOperationResult.NotFound(announcementId);
        }

        if (announcement.Revision != expectedRevision)
        {
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }

        long resultingRevision = checked(announcement.Revision + 1);
        dbContext.Announcements.Remove(announcement);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.AnnouncementDeleted,
                AuditOutcome.Succeeded,
                actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AnnouncementOperationResult.Succeeded(
                announcementId,
                resultingRevision);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }
    }

    private async Task<AnnouncementOperationResult> MutateAsync(
        Guid announcementId,
        Guid actorUserId,
        Func<Announcement, AnnouncementDomainResult> mutation,
        AuditEventCode auditEventCode,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Announcement? announcement = await dbContext.Announcements
            .SingleOrDefaultAsync(value => value.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return AnnouncementOperationResult.NotFound(announcementId);
        }

        AnnouncementDomainResult domainResult = mutation(announcement);
        AnnouncementOperationResult? rejected = domainResult.Status switch
        {
            AnnouncementDomainStatus.Succeeded => null,
            AnnouncementDomainStatus.StaleRevision =>
                AnnouncementOperationResult.StaleRevision(announcementId),
            AnnouncementDomainStatus.InvalidTransition =>
                AnnouncementOperationResult.InvalidTransition(announcementId),
            AnnouncementDomainStatus.ValidationFailed =>
                AnnouncementOperationResult.ValidationFailed(
                    announcementId,
                    domainResult.ValidationErrors),
            _ => throw new InvalidOperationException(
                "The announcement mutation returned an unsupported status."),
        };
        if (rejected is not null)
        {
            return rejected;
        }

        await auditWriter.WriteAsync(
            new AuditEvent(
                auditEventCode,
                AuditOutcome.Succeeded,
                actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AnnouncementOperationResult.Succeeded(
                announcement.Id,
                announcement.Revision);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }
    }

    private static string NormalizeSearch(string? search)
    {
        string normalized = search?.Trim() ?? string.Empty;
        Rune[] runes = normalized.EnumerateRunes().ToArray();
        return runes.Length <= AnnouncementPage.MaximumSearchScalarCount
            ? normalized
            : string.Concat(
                runes
                    .Take(AnnouncementPage.MaximumSearchScalarCount)
                    .Select(value => value.ToString()));
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
