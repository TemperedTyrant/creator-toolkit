namespace TemperedTyrant.CreatorToolkit.Core.Publications;

public interface IPublicationHistoryService
{
    Task<PublicationHistoryPage> ListAsync(
        PublicationHistoryRequest request,
        CancellationToken cancellationToken = default);

    Task<PublicationHistoryDetails?> GetAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default);

    Task<PublicationCancellationResult> CancelAsync(
        Guid publicationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed record PublicationHistoryRequest(
    PublicationStatus? Status,
    PublicationProvider? Provider,
    DateTimeOffset? RequestedFromUtc,
    DateTimeOffset? RequestedToUtc,
    int Page,
    int PageSize = PublicationHistoryPage.DefaultPageSize);

public sealed record PublicationHistoryPage(
    IReadOnlyList<PublicationHistoryItem> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
}

public sealed record PublicationHistoryItem(
    Guid Id,
    Guid? AnnouncementId,
    long AnnouncementRevision,
    PublicationProvider Provider,
    DateTimeOffset RequestedAtUtc,
    PublicationStatus Status,
    int TotalDeliveryCount,
    int SuccessfulDeliveryCount,
    int FailedDeliveryCount,
    int CancelledDeliveryCount,
    long Revision);

public sealed record PublicationHistoryDetails(
    PublicationHistoryItem Publication,
    DateTimeOffset? CancellationRequestedAtUtc,
    Guid RequestedByUserId,
    IReadOnlyList<PublicationDeliveryDetails> Deliveries);

public sealed record PublicationDeliveryDetails(
    Guid Id,
    Guid? LocalDestinationId,
    string ServerName,
    string ChannelName,
    PublicationDeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    string? SafeOutcome,
    string? ExternalMessageId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<PublicationAttemptDetails> Attempts);

public sealed record PublicationAttemptDetails(
    int AttemptNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string SafeOutcome,
    DateTimeOffset? RetryScheduledForUtc,
    string? ExternalMessageId,
    string? DiagnosticReference);

public enum PublicationCancellationResult
{
    Succeeded = 1,
    NotFound = 2,
    StaleRevision = 3,
    AlreadyRequestedOrTerminal = 4,
}
