using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Setup;

public sealed class BootstrapCapabilityIssuer(
    CreatorToolkitDbContext dbContext,
    SecurityOperationCoordinator coordinator,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(30);

    public Task<BootstrapCapabilityIssueResult> IssueAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
        {
            throw new ArgumentException("A capability hash must contain 32 bytes.", nameof(tokenHash));
        }

        return coordinator.ExecuteAsync(
            operationToken => IssueWithinLockAsync(tokenHash, operationToken),
            cancellationToken);
    }

    private async Task<BootstrapCapabilityIssueResult> IssueWithinLockAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        InstallationState installation = await dbContext.InstallationStates
            .SingleAsync(
                state => state.Id == InstallationState.SingletonId,
                cancellationToken);
        if (installation.InitializedAtUtc is not null)
        {
            return BootstrapCapabilityIssueResult.InstallationInitialized;
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        SecurityCapability? previous = await dbContext.SecurityCapabilities
            .SingleOrDefaultAsync(
                capability =>
                    capability.Purpose == CapabilityPurpose.BootstrapOwner
                    && capability.ActiveSlot == SecurityCapability.BootstrapOwnerActiveSlot,
                cancellationToken);

        if (previous is not null)
        {
            previous.Revoke(now);
        }

        dbContext.SecurityCapabilities.Add(
            SecurityCapability.CreateBootstrapOwner(
                tokenHash,
                now,
                now.Add(CapabilityLifetime)));
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.BootstrapCapabilityCreated,
                AuditOutcome.Succeeded,
                ReasonCode: previous is null ? null : AuditReasonCode.Replaced),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return previous is null
            ? BootstrapCapabilityIssueResult.Created
            : BootstrapCapabilityIssueResult.Replaced;
    }
}

public enum BootstrapCapabilityIssueResult
{
    Created = 1,
    Replaced = 2,
    InstallationInitialized = 3,
}
