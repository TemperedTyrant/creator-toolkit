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
}
