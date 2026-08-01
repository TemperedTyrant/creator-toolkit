namespace TemperedTyrant.CreatorToolkit.Core.Audit;

public sealed class AuditRecord
{
    private AuditRecord()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventCode { get; private set; } = string.Empty;

    public Guid? ActorUserId { get; private set; }

    public Guid? TargetUserId { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public string? ReasonCode { get; private set; }

    public string? DiagnosticReference { get; private set; }

    public static AuditRecord Create(
        AuditEvent auditEvent,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        return new AuditRecord
        {
            Id = Guid.NewGuid(),
            OccurredAtUtc = occurredAtUtc,
            EventCode = auditEvent.EventCode switch
            {
                AuditEventCode.ProtectedOperation => "security.protected-operation",
                AuditEventCode.BootstrapCapabilityCreated =>
                    "security.bootstrap-capability-created",
                AuditEventCode.InitialOwnerCreated => "identity.initial-owner-created",
                AuditEventCode.LoginSucceeded => "identity.login-succeeded",
                AuditEventCode.LogoutSucceeded => "identity.logout-succeeded",
                AuditEventCode.PasswordChanged => "identity.password-changed",
                AuditEventCode.LoginRejected => "identity.login-rejected",
                AuditEventCode.PasswordChangeRejected => "identity.password-change-rejected",
                AuditEventCode.PendingUserCreated => "identity.pending-user-created",
                AuditEventCode.ActivationCapabilityCreated =>
                    "identity.activation-capability-created",
                AuditEventCode.UserActivated => "identity.user-activated",
                AuditEventCode.UserRoleChanged => "identity.user-role-changed",
                AuditEventCode.UserDisabled => "identity.user-disabled",
                AuditEventCode.UserDeleted => "identity.user-deleted",
                AuditEventCode.OwnershipTransferred => "identity.ownership-transferred",
                AuditEventCode.OwnerRecoveryCapabilityCreated =>
                    "identity.owner-recovery-capability-created",
                AuditEventCode.OwnerRecovered => "identity.owner-recovered",
                AuditEventCode.OwnershipTransferRejected =>
                    "identity.ownership-transfer-rejected",
                AuditEventCode.AnnouncementCreated => "announcement.created",
                AuditEventCode.AnnouncementUpdated => "announcement.updated",
                AuditEventCode.AnnouncementArchived => "announcement.archived",
                AuditEventCode.AnnouncementRestored => "announcement.restored",
                AuditEventCode.AnnouncementDeleted => "announcement.deleted",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(auditEvent),
                    "The audit event code is not supported."),
            },
            ActorUserId = auditEvent.ActorUserId,
            TargetUserId = auditEvent.TargetUserId,
            Outcome = auditEvent.Outcome switch
            {
                AuditOutcome.Succeeded => "succeeded",
                AuditOutcome.Rejected => "rejected",
                AuditOutcome.Failed => "failed",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(auditEvent),
                    "The audit outcome is not supported."),
            },
            ReasonCode = auditEvent.ReasonCode switch
            {
                null => null,
                AuditReasonCode.Conflict => "conflict",
                AuditReasonCode.UnexpectedFailure => "unexpected-failure",
                AuditReasonCode.Replaced => "replaced",
                AuditReasonCode.InvalidCredentials => "invalid-credentials",
                AuditReasonCode.LockedOut => "locked-out",
                AuditReasonCode.Disabled => "disabled",
                AuditReasonCode.ValidationFailed => "validation-failed",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(auditEvent),
                    "The audit reason code is not supported."),
            },
            DiagnosticReference = auditEvent.DiagnosticReference?.Value,
        };
    }
}
