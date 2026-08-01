namespace TemperedTyrant.CreatorToolkit.Core.Publications;

public sealed class Publication
{
    private Publication()
    {
    }

    public Guid Id { get; private set; }

    public Guid? AnnouncementId { get; private set; }

    public long AnnouncementRevision { get; private set; }

    public PublicationProvider Provider { get; private set; }

    public Guid SubmissionId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public PublicationStatus Status { get; private set; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; private set; }

    public int TotalDeliveryCount { get; private set; }

    public int SuccessfulDeliveryCount { get; private set; }

    public int FailedDeliveryCount { get; private set; }

    public int CancelledDeliveryCount { get; private set; }

    public long Revision { get; private set; }

    public ICollection<PublicationDelivery> Deliveries { get; private set; } = [];

    public static Publication Create(
        Guid id,
        Guid announcementId,
        long announcementRevision,
        Guid submissionId,
        Guid requestedByUserId,
        int deliveryCount,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(announcementId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(announcementRevision, 1);
        ArgumentOutOfRangeException.ThrowIfEqual(submissionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(requestedByUserId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(deliveryCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deliveryCount, 10);
        DateTimeOffset utc = now.ToUniversalTime();
        return new Publication
        {
            Id = id,
            AnnouncementId = announcementId,
            AnnouncementRevision = announcementRevision,
            Provider = PublicationProvider.Discord,
            SubmissionId = submissionId,
            RequestedByUserId = requestedByUserId,
            RequestedAtUtc = utc,
            UpdatedAtUtc = utc,
            Status = PublicationStatus.Queued,
            TotalDeliveryCount = deliveryCount,
            Revision = 1,
        };
    }

    public PublicationMutationResult RequestCancellation(
        long expectedRevision,
        DateTimeOffset now)
    {
        if (Revision != expectedRevision)
        {
            return PublicationMutationResult.StaleRevision;
        }

        if (IsTerminal(Status) || CancellationRequestedAtUtc is not null)
        {
            return PublicationMutationResult.InvalidTransition;
        }

        CancellationRequestedAtUtc = now.ToUniversalTime();
        Status = PublicationStatus.Cancelling;
        UpdatedAtUtc = now.ToUniversalTime();
        Revision = checked(Revision + 1);

        return PublicationMutationResult.Succeeded;
    }

    public void Recalculate(IReadOnlyCollection<PublicationDelivery> deliveries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        if (deliveries.Count != TotalDeliveryCount)
        {
            throw new InvalidOperationException("The publication delivery set is incomplete.");
        }

        int succeeded = deliveries.Count(value => value.Status == PublicationDeliveryStatus.Succeeded);
        int failed = deliveries.Count(value => value.Status == PublicationDeliveryStatus.FailedPermanent);
        int cancelled = deliveries.Count(value => value.Status == PublicationDeliveryStatus.Cancelled);
        PublicationStatus status = CalculateStatus(deliveries, CancellationRequestedAtUtc is not null);
        if (SuccessfulDeliveryCount == succeeded
            && FailedDeliveryCount == failed
            && CancelledDeliveryCount == cancelled
            && Status == status)
        {
            return;
        }

        SuccessfulDeliveryCount = succeeded;
        FailedDeliveryCount = failed;
        CancelledDeliveryCount = cancelled;
        Status = status;
        UpdatedAtUtc = now.ToUniversalTime();
        Revision = checked(Revision + 1);
    }

    public static PublicationStatus CalculateStatus(
        IReadOnlyCollection<PublicationDelivery> deliveries,
        bool cancellationRequested)
    {
        if (deliveries.Count == 0)
        {
            throw new ArgumentException("At least one delivery is required.", nameof(deliveries));
        }

        bool anyActive = deliveries.Any(value => !value.IsTerminal);
        if (cancellationRequested && anyActive)
        {
            return PublicationStatus.Cancelling;
        }

        if (deliveries.Any(value => value.Status == PublicationDeliveryStatus.Leased))
        {
            return PublicationStatus.Processing;
        }

        if (deliveries.Any(value => value.Status == PublicationDeliveryStatus.RetryScheduled))
        {
            return PublicationStatus.RetryScheduled;
        }

        if (deliveries.Any(value => value.Status == PublicationDeliveryStatus.Queued))
        {
            return PublicationStatus.Queued;
        }

        int succeeded = deliveries.Count(value => value.Status == PublicationDeliveryStatus.Succeeded);
        int failed = deliveries.Count(value => value.Status == PublicationDeliveryStatus.FailedPermanent);
        int cancelled = deliveries.Count(value => value.Status == PublicationDeliveryStatus.Cancelled);
        if (succeeded == deliveries.Count)
        {
            return PublicationStatus.Succeeded;
        }

        if (succeeded > 0)
        {
            return PublicationStatus.PartiallySucceeded;
        }

        if (failed == deliveries.Count || failed > 0)
        {
            return PublicationStatus.Failed;
        }

        if (cancelled == deliveries.Count)
        {
            return PublicationStatus.Cancelled;
        }

        throw new InvalidOperationException("The publication has an unsupported delivery state combination.");
    }

    public static bool IsTerminal(PublicationStatus status) => status is
        PublicationStatus.Succeeded
        or PublicationStatus.PartiallySucceeded
        or PublicationStatus.Failed
        or PublicationStatus.Cancelled;
}

public sealed class PublicationDelivery
{
    private PublicationDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid PublicationId { get; private set; }

    public Publication Publication { get; private set; } = null!;

    public Guid? LocalDestinationId { get; private set; }

    public string ProviderDestinationId { get; private set; } = string.Empty;

    public string ServerNameSnapshot { get; private set; } = string.Empty;

    public string ChannelNameSnapshot { get; private set; } = string.Empty;

    public PublicationDeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public string StableNonce { get; private set; } = string.Empty;

    public string? LastSafeOutcome { get; private set; }

    public string? ExternalMessageId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public long Revision { get; private set; }

    public ICollection<PublicationAttempt> Attempts { get; private set; } = [];

    public bool IsTerminal => Status is PublicationDeliveryStatus.Succeeded
        or PublicationDeliveryStatus.FailedPermanent
        or PublicationDeliveryStatus.Cancelled;

    public static PublicationDelivery Create(
        Guid id,
        Guid publicationId,
        Guid localDestinationId,
        string providerDestinationId,
        string serverNameSnapshot,
        string channelNameSnapshot,
        string stableNonce,
        DateTimeOffset now)
    {
        return new PublicationDelivery
        {
            Id = id,
            PublicationId = publicationId,
            LocalDestinationId = localDestinationId,
            ProviderDestinationId = RequireBounded(providerDestinationId, 20),
            ServerNameSnapshot = Snapshot(serverNameSnapshot, "Discord server"),
            ChannelNameSnapshot = Snapshot(channelNameSnapshot, "Discord channel"),
            Status = PublicationDeliveryStatus.Queued,
            NextAttemptAtUtc = now.ToUniversalTime(),
            StableNonce = RequireBounded(stableNonce, 25),
            Revision = 1,
        };
    }

    public bool TryClaim(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        bool due = (Status is PublicationDeliveryStatus.Queued or PublicationDeliveryStatus.RetryScheduled)
            && NextAttemptAtUtc <= now;
        bool expired = Status == PublicationDeliveryStatus.Leased
            && LeaseExpiresAtUtc <= now;
        if (!due && !expired)
        {
            return false;
        }

        LeaseOwner = RequireBounded(leaseOwner, 64);
        LeaseExpiresAtUtc = now.ToUniversalTime() + leaseDuration;
        Status = PublicationDeliveryStatus.Leased;
        StartedAtUtc ??= now.ToUniversalTime();
        AttemptCount = checked(AttemptCount + 1);
        Revision = checked(Revision + 1);
        return true;
    }

    public bool Complete(
        string leaseOwner,
        long expectedRevision,
        string safeOutcome,
        string? externalMessageId,
        DateTimeOffset now)
    {
        if (!OwnsLease(leaseOwner, expectedRevision))
        {
            return false;
        }

        Status = PublicationDeliveryStatus.Succeeded;
        LastSafeOutcome = RequireBounded(safeOutcome, 64);
        ExternalMessageId = OptionalBounded(externalMessageId, 20);
        CompletedAtUtc = now.ToUniversalTime();
        ClearLease();
        Revision = checked(Revision + 1);
        return true;
    }

    public bool ScheduleRetry(
        string leaseOwner,
        long expectedRevision,
        string safeOutcome,
        DateTimeOffset retryAtUtc)
    {
        if (!OwnsLease(leaseOwner, expectedRevision))
        {
            return false;
        }

        Status = PublicationDeliveryStatus.RetryScheduled;
        LastSafeOutcome = RequireBounded(safeOutcome, 64);
        NextAttemptAtUtc = retryAtUtc.ToUniversalTime();
        ClearLease();
        Revision = checked(Revision + 1);
        return true;
    }

    public bool FailPermanent(
        string leaseOwner,
        long expectedRevision,
        string safeOutcome,
        DateTimeOffset now)
    {
        if (!OwnsLease(leaseOwner, expectedRevision))
        {
            return false;
        }

        Status = PublicationDeliveryStatus.FailedPermanent;
        LastSafeOutcome = RequireBounded(safeOutcome, 64);
        CompletedAtUtc = now.ToUniversalTime();
        ClearLease();
        Revision = checked(Revision + 1);
        return true;
    }

    public bool CancelPending(DateTimeOffset now)
    {
        if (Status is not PublicationDeliveryStatus.Queued
            and not PublicationDeliveryStatus.RetryScheduled)
        {
            return false;
        }

        Status = PublicationDeliveryStatus.Cancelled;
        LastSafeOutcome = "cancelled";
        CompletedAtUtc = now.ToUniversalTime();
        ClearLease();
        Revision = checked(Revision + 1);
        return true;
    }

    public bool CancelLeased(string leaseOwner, long expectedRevision, DateTimeOffset now)
    {
        if (!OwnsLease(leaseOwner, expectedRevision))
        {
            return false;
        }

        Status = PublicationDeliveryStatus.Cancelled;
        LastSafeOutcome = "cancelled";
        CompletedAtUtc = now.ToUniversalTime();
        ClearLease();
        Revision = checked(Revision + 1);
        return true;
    }

    private bool OwnsLease(string owner, long expectedRevision) =>
        Status == PublicationDeliveryStatus.Leased
        && Revision == expectedRevision
        && string.Equals(LeaseOwner, owner, StringComparison.Ordinal);

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    private static string Snapshot(string value, string fallback)
    {
        string normalized = value.Trim();
        return normalized.Length is >= 1 and <= 100 ? normalized : fallback;
    }

    private static string RequireBounded(string value, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length <= maximum
            ? value
            : throw new ArgumentException("The value exceeds its safe bound.", nameof(value));
    }

    private static string? OptionalBounded(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireBounded(value, maximum);
}

public sealed class PublicationAttempt
{
    private PublicationAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid PublicationDeliveryId { get; private set; }

    public PublicationDelivery Delivery { get; private set; } = null!;

    public int AttemptNumber { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string SafeOutcome { get; private set; } = string.Empty;

    public DateTimeOffset? RetryScheduledForUtc { get; private set; }

    public string? ExternalMessageId { get; private set; }

    public string? DiagnosticReference { get; private set; }

    public static PublicationAttempt Start(
        Guid id,
        Guid deliveryId,
        int attemptNumber,
        DateTimeOffset now) => new()
        {
            Id = id,
            PublicationDeliveryId = deliveryId,
            AttemptNumber = attemptNumber,
            StartedAtUtc = now.ToUniversalTime(),
            SafeOutcome = "in-progress",
        };

    public void Finish(
        string safeOutcome,
        DateTimeOffset now,
        DateTimeOffset? retryAt = null,
        string? externalMessageId = null,
        string? diagnosticReference = null)
    {
        SafeOutcome = safeOutcome.Length <= 64 ? safeOutcome : "unexpected-failure";
        CompletedAtUtc = now.ToUniversalTime();
        RetryScheduledForUtc = retryAt?.ToUniversalTime();
        ExternalMessageId = externalMessageId is { Length: <= 20 } ? externalMessageId : null;
        DiagnosticReference = diagnosticReference is { Length: <= 64 } ? diagnosticReference : null;
    }
}

public static class PublicationRetryPolicy
{
    public const int MaximumAttempts = 4;

    public static bool IsTransient(PublicationSafeOutcome outcome) => outcome is
        PublicationSafeOutcome.RateLimited
        or PublicationSafeOutcome.DiscordUnavailable
        or PublicationSafeOutcome.TimedOut
        or PublicationSafeOutcome.ConnectionFailure;

    public static TimeSpan DelayAfterAttempt(int attemptNumber, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } requestedDelay && requestedDelay > TimeSpan.Zero)
        {
            return requestedDelay <= TimeSpan.FromMinutes(10)
                ? requestedDelay
                : TimeSpan.FromMinutes(10);
        }

        return attemptNumber switch
        {
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(10),
        };
    }
}

public enum PublicationProvider { Discord = 1 }

public enum PublicationStatus
{
    Queued = 1,
    Processing = 2,
    RetryScheduled = 3,
    Succeeded = 4,
    PartiallySucceeded = 5,
    Failed = 6,
    Cancelling = 7,
    Cancelled = 8,
}

public enum PublicationDeliveryStatus
{
    Queued = 1,
    Leased = 2,
    RetryScheduled = 3,
    Succeeded = 4,
    FailedPermanent = 5,
    Cancelled = 6,
}

public enum PublicationSafeOutcome
{
    Success = 1,
    RateLimited = 2,
    MissingPermission = 3,
    DestinationUnavailable = 4,
    AuthenticationFailed = 5,
    ValidationRejected = 6,
    DiscordUnavailable = 7,
    TimedOut = 8,
    Cancelled = 9,
    UnexpectedFailure = 10,
    ConnectionFailure = 11,
    ProtectedPayloadInvalid = 12,
}

public enum PublicationMutationResult
{
    Succeeded = 1,
    StaleRevision = 2,
    InvalidTransition = 3,
}
