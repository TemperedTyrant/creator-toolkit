using TemperedTyrant.CreatorToolkit.Core.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.Core.Audit;

public sealed record AuditEvent(
    AuditEventCode EventCode,
    AuditOutcome Outcome,
    Guid? ActorUserId = null,
    Guid? TargetUserId = null,
    AuditReasonCode? ReasonCode = null,
    DiagnosticReference? DiagnosticReference = null);

public enum AuditEventCode
{
    ProtectedOperation = 1,
    BootstrapCapabilityCreated = 2,
    InitialOwnerCreated = 3,
    LoginSucceeded = 4,
    LogoutSucceeded = 5,
    PasswordChanged = 6,
    LoginRejected = 7,
    PasswordChangeRejected = 8,
    PendingUserCreated = 9,
    ActivationCapabilityCreated = 10,
    UserActivated = 11,
    UserRoleChanged = 12,
    UserDisabled = 13,
    UserDeleted = 14,
    OwnershipTransferred = 15,
    OwnerRecoveryCapabilityCreated = 16,
    OwnerRecovered = 17,
    OwnershipTransferRejected = 18,
    AnnouncementCreated = 19,
    AnnouncementUpdated = 20,
    AnnouncementArchived = 21,
    AnnouncementRestored = 22,
    AnnouncementDeleted = 23,
    DiscordConnectionCreated = 24,
    DiscordTokenReplaced = 25,
    DiscordConnectionEnabled = 26,
    DiscordConnectionDisabled = 27,
    DiscordConnectionDeleted = 28,
    DiscordDestinationAdded = 29,
    DiscordDestinationEnabled = 30,
    DiscordDestinationDisabled = 31,
    DiscordDestinationDeleted = 32,
    DiscordDestinationTestSent = 33,
    DiscordPublicationRequested = 34,
    DiscordPublicationChannelSucceeded = 35,
    DiscordPublicationChannelFailed = 36,
    PublicationQueued = 37,
    PublicationCancellationRequested = 38,
    PublicationDeliveryClaimed = 39,
    PublicationRetryScheduled = 40,
    PublicationDeliveryCancelled = 41,
    PublicationFinalized = 42,
    PublicationPayloadRemoved = 43,
}

public enum AuditOutcome
{
    Succeeded = 1,
    Rejected = 2,
    Failed = 3,
}

public enum AuditReasonCode
{
    Conflict = 1,
    UnexpectedFailure = 2,
    Replaced = 3,
    InvalidCredentials = 4,
    LockedOut = 5,
    Disabled = 6,
    ValidationFailed = 7,
}
