namespace TemperedTyrant.CreatorToolkit.Core.Announcements;

public interface IAnnouncementService
{
    Task<AnnouncementPage> ListAsync(
        AnnouncementListRequest request,
        CancellationToken cancellationToken = default);

    Task<AnnouncementDetails?> GetAsync(
        Guid announcementId,
        CancellationToken cancellationToken = default);

    Task<AnnouncementOperationResult> CreateAsync(
        Guid announcementId,
        string? title,
        string? messageContent,
        Guid actorUserId,
        IReadOnlyList<AnnouncementMediaUpload>? media = null,
        CancellationToken cancellationToken = default);

    Task<AnnouncementOperationResult> UpdateAsync(
        Guid announcementId,
        string? title,
        string? messageContent,
        long expectedRevision,
        Guid actorUserId,
        AnnouncementMediaChangeSet? mediaChanges = null,
        CancellationToken cancellationToken = default);

    Task<AnnouncementMediaContent?> GetMediaContentAsync(
        Guid announcementId,
        Guid mediaId,
        CancellationToken cancellationToken = default);

    Task<AnnouncementOperationResult> ArchiveAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<AnnouncementOperationResult> RestoreAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<AnnouncementOperationResult> DeleteAsync(
        Guid announcementId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed record AnnouncementListRequest(
    string? Search,
    AnnouncementStatusFilter Status,
    int Page,
    int PageSize = AnnouncementPage.DefaultPageSize);

public enum AnnouncementStatusFilter
{
    Draft = 1,
    Archived = 2,
    All = 3,
}

public sealed record AnnouncementPage(
    IReadOnlyList<AnnouncementSummary> Items,
    string Search,
    AnnouncementStatusFilter Status,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchScalarCount = 200;
}

public sealed record AnnouncementSummary(
    Guid Id,
    string Title,
    AnnouncementStatus Status,
    DateTimeOffset UpdatedAtUtc,
    long Revision);

public sealed record AnnouncementDetails(
    Guid Id,
    string Title,
    string MessageContent,
    AnnouncementStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    long Revision,
    IReadOnlyList<AnnouncementMediaSummary> Media);

public sealed record AnnouncementMediaSummary(
    Guid Id,
    int SortOrder,
    string ContentType,
    int ByteLength,
    string GeneratedFileName,
    string? AltText,
    bool IsSpoiler,
    AnnouncementMediaPresentation Presentation,
    long Revision);

public sealed record AnnouncementMediaContent(
    byte[] Bytes,
    string ContentType,
    string GeneratedFileName);

public sealed record AnnouncementOperationResult(
    AnnouncementOperationStatus Status,
    Guid AnnouncementId,
    long? Revision,
    IReadOnlyList<AnnouncementValidationError> ValidationErrors)
{
    public static AnnouncementOperationResult Succeeded(
        Guid announcementId,
        long revision) =>
        new(AnnouncementOperationStatus.Succeeded, announcementId, revision, []);

    public static AnnouncementOperationResult DuplicateSubmission(
        Guid announcementId,
        long revision) =>
        new(
            AnnouncementOperationStatus.DuplicateSubmission,
            announcementId,
            revision,
            []);

    public static AnnouncementOperationResult NotFound(Guid announcementId) =>
        new(AnnouncementOperationStatus.NotFound, announcementId, null, []);

    public static AnnouncementOperationResult StaleRevision(Guid announcementId) =>
        new(AnnouncementOperationStatus.StaleRevision, announcementId, null, []);

    public static AnnouncementOperationResult InvalidTransition(Guid announcementId) =>
        new(AnnouncementOperationStatus.InvalidTransition, announcementId, null, []);

    public static AnnouncementOperationResult ValidationFailed(
        Guid announcementId,
        IReadOnlyList<AnnouncementValidationError> errors) =>
        new(AnnouncementOperationStatus.ValidationFailed, announcementId, null, errors);
}

public enum AnnouncementOperationStatus
{
    Succeeded = 1,
    DuplicateSubmission = 2,
    NotFound = 3,
    StaleRevision = 4,
    InvalidTransition = 5,
    ValidationFailed = 6,
}
