using System.Text;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;

internal sealed class AnnouncementService(
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter,
    AnnouncementMediaProtector mediaProtector,
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
                    || EF.Functions.Like(value.MessageContent, pattern, "\\"));
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
                    value.MessageContent,
                    value.Status,
                    value.CreatedAtUtc,
                    value.UpdatedAtUtc,
                    value.CreatedByUserId,
                    value.UpdatedByUserId,
                    value.Revision,
                    value.Media
                        .OrderBy(media => media.SortOrder)
                        .ThenBy(media => media.Id)
                        .Select(media => new AnnouncementMediaSummary(
                            media.Id,
                            media.SortOrder,
                            media.ContentType,
                            media.ByteLength,
                            media.GeneratedFileName,
                            media.AltText,
                            media.IsSpoiler,
                            media.Presentation,
                            media.Revision))
                        .ToArray()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<AnnouncementOperationResult> CreateAsync(
        Guid announcementId,
        string? title,
        string? messageContent,
        Guid actorUserId,
        IReadOnlyList<AnnouncementMediaUpload>? media = null,
        CancellationToken cancellationToken = default)
    {
        AnnouncementCreationResult creation = Announcement.Create(
            announcementId,
            title,
            messageContent,
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

        IReadOnlyList<PreparedMedia> prepared;
        try
        {
            prepared = PrepareAddedMedia(announcementId, media ?? [], []);
        }
        catch (AnnouncementMediaValidationException exception)
        {
            return MediaValidationFailed(announcementId, exception.Message);
        }

        Announcement announcement = creation.Announcement!;
        dbContext.Announcements.Add(announcement);
        foreach (PreparedMedia item in prepared)
        {
            dbContext.AnnouncementMediaAssets.Add(CreateMediaEntity(announcementId, item));
            await WriteAuditAsync(AuditEventCode.AnnouncementMediaAdded, actorUserId, cancellationToken);
        }
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

    public async Task<AnnouncementOperationResult> UpdateAsync(
        Guid announcementId,
        string? title,
        string? messageContent,
        long expectedRevision,
        Guid actorUserId,
        AnnouncementMediaChangeSet? mediaChanges = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Announcement? announcement = await dbContext.Announcements
            .Include(value => value.Media)
            .SingleOrDefaultAsync(value => value.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return AnnouncementOperationResult.NotFound(announcementId);
        }

        if (announcement.Revision != expectedRevision)
        {
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }

        if (announcement.Status != AnnouncementStatus.Draft)
        {
            return AnnouncementOperationResult.InvalidTransition(announcementId);
        }

        if (mediaChanges is not null && MediaStateIsStale(announcement, mediaChanges))
        {
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }

        MediaUpdatePlan? mediaPlan;
        try
        {
            mediaPlan = BuildMediaUpdatePlan(announcement, mediaChanges);
        }
        catch (AnnouncementMediaValidationException exception)
        {
            return MediaValidationFailed(announcementId, exception.Message);
        }

        AnnouncementDomainResult domainResult = announcement.Update(
            title,
            messageContent,
            expectedRevision,
            actorUserId,
            timeProvider.GetUtcNow());
        if (domainResult.Status == AnnouncementDomainStatus.ValidationFailed)
        {
            return AnnouncementOperationResult.ValidationFailed(
                announcementId,
                domainResult.ValidationErrors);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (mediaPlan is not null)
        {
            foreach (AnnouncementMediaAsset removed in mediaPlan.Removed)
            {
                dbContext.AnnouncementMediaAssets.Remove(removed);
                await WriteAuditAsync(AuditEventCode.AnnouncementMediaRemoved, actorUserId, cancellationToken);
            }

            foreach ((AnnouncementMediaAsset asset, AnnouncementMediaEdit edit) in mediaPlan.Updated)
            {
                bool reordered = asset.SortOrder != edit.SortOrder;
                bool metadataChanged = !string.Equals(asset.AltText, NormalizeAlt(edit.AltText), StringComparison.Ordinal)
                    || asset.IsSpoiler != edit.IsSpoiler
                    || asset.Presentation != edit.Presentation;
                if (!asset.UpdateMetadata(
                    edit.Revision,
                    edit.SortOrder,
                    edit.AltText,
                    edit.IsSpoiler,
                    edit.Presentation,
                    now))
                {
                    return AnnouncementOperationResult.StaleRevision(announcementId);
                }

                if (reordered)
                {
                    await WriteAuditAsync(AuditEventCode.AnnouncementMediaReordered, actorUserId, cancellationToken);
                }

                if (metadataChanged)
                {
                    await WriteAuditAsync(AuditEventCode.AnnouncementMediaMetadataChanged, actorUserId, cancellationToken);
                }
            }

            foreach (PreparedMedia added in mediaPlan.Added)
            {
                dbContext.AnnouncementMediaAssets.Add(CreateMediaEntity(announcementId, added));
                await WriteAuditAsync(AuditEventCode.AnnouncementMediaAdded, actorUserId, cancellationToken);
            }
        }

        await WriteAuditAsync(AuditEventCode.AnnouncementUpdated, actorUserId, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AnnouncementOperationResult.Succeeded(announcement.Id, announcement.Revision);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }
    }

    public async Task<AnnouncementMediaContent?> GetMediaContentAsync(
        Guid announcementId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        AnnouncementMediaAsset? media = await dbContext.AnnouncementMediaAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == mediaId && value.AnnouncementId == announcementId,
                cancellationToken);
        if (media is null)
        {
            return null;
        }

        try
        {
            return new AnnouncementMediaContent(
                mediaProtector.Unprotect(media),
                media.ContentType,
                media.GeneratedFileName);
        }
        catch (AnnouncementMediaUnavailableException)
        {
            return null;
        }
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
            .Include(value => value.Media)
            .SingleOrDefaultAsync(value => value.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return AnnouncementOperationResult.NotFound(announcementId);
        }

        if (announcement.Revision != expectedRevision)
        {
            return AnnouncementOperationResult.StaleRevision(announcementId);
        }

        bool hasActivePublication = await dbContext.Publications.AsNoTracking()
            .AnyAsync(
                value => value.AnnouncementId == announcementId
                    && value.Status != PublicationStatus.Succeeded
                    && value.Status != PublicationStatus.PartiallySucceeded
                    && value.Status != PublicationStatus.Failed
                    && value.Status != PublicationStatus.Cancelled,
                cancellationToken);
        if (hasActivePublication)
        {
            return AnnouncementOperationResult.InvalidTransition(announcementId);
        }

        long resultingRevision = checked(announcement.Revision + 1);
        dbContext.Announcements.Remove(announcement);
        foreach (AnnouncementMediaAsset _ in announcement.Media)
        {
            await WriteAuditAsync(
                AuditEventCode.AnnouncementMediaRemoved,
                actorUserId,
                cancellationToken);
        }

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

    private static MediaUpdatePlan? BuildMediaUpdatePlan(
        Announcement announcement,
        AnnouncementMediaChangeSet? changes)
    {
        if (changes is null)
        {
            return null;
        }

        if (changes.Existing.Count != announcement.Media.Count
            || changes.Existing.Select(value => value.Id).Distinct().Count() != changes.Existing.Count)
        {
            throw new AnnouncementMediaValidationException(
                "The saved image list changed. Reload the draft and try again.");
        }

        Dictionary<Guid, AnnouncementMediaAsset> current = announcement.Media
            .ToDictionary(value => value.Id);
        foreach (AnnouncementMediaEdit edit in changes.Existing)
        {
            if (!current.TryGetValue(edit.Id, out AnnouncementMediaAsset? asset)
                || asset.Revision != edit.Revision)
            {
                throw new AnnouncementMediaValidationException(
                    "The saved image list changed. Reload the draft and try again.");
            }

            ValidateMetadata(edit.SortOrder, edit.AltText, edit.Presentation);
        }

        AnnouncementMediaEdit[] retained = changes.Existing.Where(value => !value.Remove).ToArray();
        if (retained.Length + changes.Added.Count > AnnouncementMediaAsset.MaximumAssetCount)
        {
            throw new AnnouncementMediaValidationException(
                "An announcement can contain at most four images.");
        }

        IReadOnlyList<PreparedMedia> added = PrepareAddedMedia(
            announcement.Id,
            changes.Added,
            retained.Select(value => current[value.Id].ByteLength).ToArray());
        int total = retained.Length + added.Count;
        int[] orders = retained.Select(value => value.SortOrder)
            .Concat(added.Select(value => value.SortOrder))
            .Order()
            .ToArray();
        if (!orders.SequenceEqual(Enumerable.Range(0, total)))
        {
            throw new AnnouncementMediaValidationException(
                "Image order is invalid. Reload the draft and try again.");
        }

        int featured = retained.Count(value => value.Presentation == AnnouncementMediaPresentation.FeaturedImage)
            + added.Count(value => value.Presentation == AnnouncementMediaPresentation.FeaturedImage);
        if (featured > 1)
        {
            throw new AnnouncementMediaValidationException(
                "Only one image can be the featured image.");
        }

        return new MediaUpdatePlan(
            changes.Existing.Where(value => value.Remove).Select(value => current[value.Id]).ToArray(),
            retained.Select(value => (current[value.Id], value)).ToArray(),
            added);
    }

    private static bool MediaStateIsStale(
        Announcement announcement,
        AnnouncementMediaChangeSet changes)
    {
        if (changes.Existing.Count != announcement.Media.Count
            || changes.Existing.Select(value => value.Id).Distinct().Count() != changes.Existing.Count)
        {
            return true;
        }

        Dictionary<Guid, long> current = announcement.Media.ToDictionary(value => value.Id, value => value.Revision);
        return changes.Existing.Any(value =>
            !current.TryGetValue(value.Id, out long revision) || revision != value.Revision);
    }

    private static List<PreparedMedia> PrepareAddedMedia(
        Guid announcementId,
        IReadOnlyList<AnnouncementMediaUpload> uploads,
        int[] existingByteLengths)
    {
        if (uploads.Count + existingByteLengths.Length > AnnouncementMediaAsset.MaximumAssetCount)
        {
            throw new AnnouncementMediaValidationException(
                "An announcement can contain at most four images.");
        }

        var prepared = new List<PreparedMedia>(uploads.Count);
        foreach (AnnouncementMediaUpload upload in uploads)
        {
            ValidateMetadata(upload.SortOrder, upload.AltText, upload.Presentation);
            Guid mediaId = Guid.NewGuid();
            ValidatedAnnouncementMedia validated = AnnouncementMediaValidation.Validate(upload, mediaId);
            prepared.Add(new PreparedMedia(mediaId, validated));
        }

        long combinedBytes = existingByteLengths.Sum(value => (long)value)
            + prepared.Sum(value => (long)value.Validated.Bytes.Length);
        if (combinedBytes > AnnouncementMediaAsset.MaximumCombinedBytes)
        {
            throw new AnnouncementMediaValidationException(
                "Combined announcement images must be no larger than 8 MiB.");
        }

        int featured = prepared.Count(value =>
            value.Validated.Presentation == AnnouncementMediaPresentation.FeaturedImage);
        if (existingByteLengths.Length == 0 && featured > 1)
        {
            throw new AnnouncementMediaValidationException(
                "Only one image can be the featured image.");
        }

        if (uploads.Count > 0)
        {
            int[] orders = uploads.Select(value => value.SortOrder).Order().ToArray();
            if (existingByteLengths.Length == 0
                && !orders.SequenceEqual(Enumerable.Range(0, uploads.Count))
                || orders.Distinct().Count() != orders.Length
                || orders.Any(value => value < 0))
            {
                throw new AnnouncementMediaValidationException("Image order is invalid.");
            }
        }

        return prepared;
    }

    private AnnouncementMediaAsset CreateMediaEntity(Guid announcementId, PreparedMedia prepared)
    {
        ValidatedAnnouncementMedia value = prepared.Validated;
        byte[] protectedContent = mediaProtector.Protect(
            announcementId,
            prepared.Id,
            value.Bytes);
        return AnnouncementMediaAsset.Create(
            prepared.Id,
            announcementId,
            value.SortOrder,
            protectedContent,
            value.ContentType,
            value.Bytes.Length,
            value.Sha256Digest,
            value.GeneratedFileName,
            value.AltText,
            value.IsSpoiler,
            value.Presentation,
            timeProvider.GetUtcNow());
    }

    private Task WriteAuditAsync(
        AuditEventCode code,
        Guid actorUserId,
        CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            new AuditEvent(code, AuditOutcome.Succeeded, actorUserId),
            cancellationToken);

    private static void ValidateMetadata(
        int sortOrder,
        string? altText,
        AnnouncementMediaPresentation presentation)
    {
        if (sortOrder is < 0 or >= AnnouncementMediaAsset.MaximumAssetCount
            || !Enum.IsDefined(presentation)
            || NormalizeAlt(altText)?.Length > AnnouncementMediaAsset.MaximumAltTextLength)
        {
            throw new AnnouncementMediaValidationException("Image metadata is invalid.");
        }
    }

    private static string? NormalizeAlt(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AnnouncementOperationResult MediaValidationFailed(Guid id, string message) =>
        AnnouncementOperationResult.ValidationFailed(
            id,
            [new AnnouncementValidationError("Media", message)]);

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

    private sealed record PreparedMedia(Guid Id, ValidatedAnnouncementMedia Validated)
    {
        internal int SortOrder => Validated.SortOrder;

        internal AnnouncementMediaPresentation Presentation => Validated.Presentation;
    }

    private sealed record MediaUpdatePlan(
        IReadOnlyList<AnnouncementMediaAsset> Removed,
        IReadOnlyList<(AnnouncementMediaAsset Asset, AnnouncementMediaEdit Edit)> Updated,
        IReadOnlyList<PreparedMedia> Added);
}
