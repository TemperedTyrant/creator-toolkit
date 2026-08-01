using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Publications;

public sealed partial class PublicationWorker(
    IServiceScopeFactory scopeFactory,
    PersistenceInitializationState initializationState,
    PublicationWorkerOptions options,
    TimeProvider timeProvider,
    IDiagnosticRecorder diagnosticRecorder,
    ILogger<PublicationWorker> logger) : BackgroundService
{
    private readonly string leaseOwner = WebEncoders.Base64UrlEncode(
        RandomNumberGenerator.GetBytes(18));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (initializationState.GetStatus() != PersistenceInitializationStatus.Succeeded)
        {
            return;
        }

        LogStarted(logger);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool processed;
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    processed = await scope.ServiceProvider
                        .GetRequiredService<PublicationProcessor>()
                        .ProcessNextAsync(leaseOwner, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    DiagnosticReference reference = await diagnosticRecorder.RecordAsync(
                        new UnexpectedDiagnosticEvent(
                            DiagnosticFailureKind.Infrastructure,
                            DiagnosticOperation.DiscordPublicationProcessing),
                        CancellationToken.None);
                    LogIterationFailed(logger, reference.Value);
                    processed = false;
                }

                if (!processed)
                {
                    await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            LogStopped(logger);
        }
    }

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information, Message = "Publication worker started.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Information, Message = "Publication worker stopped.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(
        EventId = 8103,
        Level = LogLevel.Error,
        Message = "Publication processing failed safely. Reference: {DiagnosticReference}.")]
    private static partial void LogIterationFailed(ILogger logger, string diagnosticReference);
}

public sealed record PublicationWorkerOptions(
    TimeSpan PollInterval,
    TimeSpan LeaseDuration,
    TimeSpan AttemptTimeout)
{
    public static PublicationWorkerOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(20));
}

internal sealed class PublicationProcessor(
    CreatorToolkitDbContext dbContext,
    PublicationPayloadProtector payloadProtector,
    IProtectedSecretValueResolver secretResolver,
    IDiscordApi discordApi,
    IAuditWriter auditWriter,
    IDiagnosticRecorder diagnosticRecorder,
    PublicationWorkerOptions options,
    TimeProvider timeProvider)
{
    internal async Task<bool> ProcessNextAsync(
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        if (await FinalizeExhaustedLeaseAsync(cancellationToken))
        {
            return true;
        }

        ClaimedDelivery? claim = await ClaimAsync(leaseOwner, cancellationToken);
        if (claim is null)
        {
            return false;
        }

        DeliveryAttemptResult result = await DeliverAsync(claim, cancellationToken);
        await PersistResultAsync(claim, result, cancellationToken);
        return true;
    }

    private async Task<bool> FinalizeExhaustedLeaseAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        PublicationDelivery? delivery = await dbContext.PublicationDeliveries
            .Include(value => value.Publication)
            .ThenInclude(value => value.Deliveries)
            .Include(value => value.Attempts)
            .Where(value => value.Status == PublicationDeliveryStatus.Leased
                && value.LeaseExpiresAtUtc <= now
                && value.AttemptCount >= PublicationRetryPolicy.MaximumAttempts)
            .OrderBy(value => value.LeaseExpiresAtUtc)
            .ThenBy(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (delivery?.LeaseOwner is null)
        {
            return false;
        }

        bool cancelled = delivery.Publication.CancellationRequestedAtUtc is not null;
        bool changed = cancelled
            ? delivery.CancelLeased(delivery.LeaseOwner, delivery.Revision, now)
            : delivery.FailPermanent(
                delivery.LeaseOwner,
                delivery.Revision,
                "maximum-attempts-exhausted",
                now);
        if (!changed)
        {
            return false;
        }

        foreach (PublicationAttempt attempt in delivery.Attempts.Where(value => value.CompletedAtUtc is null))
        {
            attempt.Finish(cancelled ? "cancelled" : "maximum-attempts-exhausted", now);
        }

        delivery.Publication.Recalculate(delivery.Publication.Deliveries.ToArray(), now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                cancelled
                    ? AuditEventCode.PublicationDeliveryCancelled
                    : AuditEventCode.DiscordPublicationChannelFailed,
                AuditOutcome.Failed,
                delivery.Publication.RequestedByUserId),
            cancellationToken);
        if (Publication.IsTerminal(delivery.Publication.Status))
        {
            PublicationPayload? payload = await dbContext.PublicationPayloads
                .SingleOrDefaultAsync(
                    value => value.PublicationId == delivery.PublicationId,
                    cancellationToken);
            if (payload is not null)
            {
                dbContext.PublicationPayloads.Remove(payload);
                await auditWriter.WriteAsync(
                    new AuditEvent(
                        AuditEventCode.PublicationPayloadRemoved,
                        AuditOutcome.Succeeded,
                        delivery.Publication.RequestedByUserId),
                    cancellationToken);
            }

            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.PublicationFinalized,
                    AuditOutcome.Succeeded,
                    delivery.Publication.RequestedByUserId),
                cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<ClaimedDelivery?> ClaimAsync(
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        PublicationDelivery? delivery = await dbContext.PublicationDeliveries
            .Include(value => value.Publication)
            .ThenInclude(value => value.Deliveries)
            .Include(value => value.Attempts)
            .Where(value =>
                ((value.Status == PublicationDeliveryStatus.Queued
                    || value.Status == PublicationDeliveryStatus.RetryScheduled)
                    && value.Publication.CancellationRequestedAtUtc == null
                    && value.NextAttemptAtUtc <= now)
                || (value.Status == PublicationDeliveryStatus.Leased
                    && value.LeaseExpiresAtUtc <= now
                    && value.AttemptCount < PublicationRetryPolicy.MaximumAttempts))
            .OrderBy(value => value.NextAttemptAtUtc)
            .ThenBy(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (delivery is null || !delivery.TryClaim(leaseOwner, now, options.LeaseDuration))
        {
            return null;
        }

        foreach (PublicationAttempt abandoned in delivery.Attempts.Where(value => value.CompletedAtUtc is null))
        {
            abandoned.Finish("abandoned", now);
        }

        var attempt = PublicationAttempt.Start(
            Guid.NewGuid(),
            delivery.Id,
            delivery.AttemptCount,
            now);
        dbContext.PublicationAttempts.Add(attempt);
        delivery.Publication.Recalculate(delivery.Publication.Deliveries.ToArray(), now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.PublicationDeliveryClaimed,
                AuditOutcome.Succeeded,
                delivery.Publication.RequestedByUserId),
            cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ClaimedDelivery(
                delivery.Id,
                delivery.PublicationId,
                delivery.Revision,
                delivery.AttemptCount,
                leaseOwner);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    private async Task<DeliveryAttemptResult> DeliverAsync(
        ClaimedDelivery claim,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        PublicationDelivery? delivery = await dbContext.PublicationDeliveries.AsNoTracking()
            .Include(value => value.Publication)
            .SingleOrDefaultAsync(value => value.Id == claim.DeliveryId, cancellationToken);
        PublicationPayload? payload = await dbContext.PublicationPayloads.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicationId == claim.PublicationId, cancellationToken);
        if (delivery is null || payload is null)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.ProtectedPayloadInvalid);
        }


        if (delivery.Publication.CancellationRequestedAtUtc is not null)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.Cancelled);
        }

        DiscordPublishRequest? request = null;
        try
        {
            request = payloadProtector.Unprotect(payload);
        }
        catch (PublicationPayloadException)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.ProtectedPayloadInvalid);
        }

        try
        {
            DiscordConnection? connection = await dbContext.DiscordConnections.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == request.ConnectionId, cancellationToken);
            DiscordDestination? destination = delivery.LocalDestinationId is null
                ? null
                : await dbContext.DiscordDestinations.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Id == delivery.LocalDestinationId, cancellationToken);
            if (connection is null || !connection.Enabled)
            {
                return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.AuthenticationFailed);
            }

            if (destination is null
                || !destination.Enabled
                || destination.DiscordConnectionId != connection.Id
                || destination.GuildId != request.GuildId
                || destination.ChannelId != delivery.ProviderDestinationId)
            {
                return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.DestinationUnavailable);
            }

            using var timeout = new CancellationTokenSource(options.AttemptTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            return await secretResolver.UseAsync(
                new SecretReference(connection.ProtectedSecretId),
                DiscordConfigurationService.SecretPurpose(connection.Id),
                (token, ct) => SendWithTokenAsync(
                    token,
                    connection,
                    destination,
                    delivery.StableNonce,
                    request,
                    ct),
                linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryAttemptResult.Transient(PublicationSafeOutcome.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return DeliveryAttemptResult.Transient(PublicationSafeOutcome.TimedOut);
        }
        catch (DiscordApiAuthenticationException)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.AuthenticationFailed);
        }
        catch (DiscordApiUnavailableException)
        {
            return DeliveryAttemptResult.Transient(PublicationSafeOutcome.DiscordUnavailable);
        }
        catch (DiscordServerInformationException exception)
        {
            return exception.Failure is DiscordServerInformationFailure.TemporarilyUnavailable
                or DiscordServerInformationFailure.ProcessingFailed
                ? DeliveryAttemptResult.Transient(PublicationSafeOutcome.DiscordUnavailable)
                : DeliveryAttemptResult.Permanent(MapDiscoveryFailure(exception.Failure));
        }
        catch (DiscordPublicationValidationException)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.ValidationRejected);
        }
        catch (Exception)
        {
            DiagnosticReference reference = await diagnosticRecorder.RecordAsync(
                new UnexpectedDiagnosticEvent(
                    DiagnosticFailureKind.Infrastructure,
                    DiagnosticOperation.DiscordPublicationProcessing),
                CancellationToken.None);
            return DeliveryAttemptResult.Permanent(
                PublicationSafeOutcome.UnexpectedFailure,
                reference.Value);
        }
        finally
        {
            if (request is not null)
            {
                foreach (DiscordValidatedImage image in request.Images)
                {
                    CryptographicOperations.ZeroMemory(image.Bytes);
                }
            }
        }
    }

    private async Task<DeliveryAttemptResult> SendWithTokenAsync(
        string token,
        DiscordConnection connection,
        DiscordDestination destination,
        string stableNonce,
        DiscordPublishRequest request,
        CancellationToken cancellationToken)
    {
        DiscordGuildDiscovery? discovery = await discordApi.DiscoverGuildAsync(
            token,
            new DiscordBotIdentity(
                connection.BotUserId,
                connection.BotUsernameSnapshot,
                connection.ApplicationId),
            request.GuildId,
            cancellationToken);
        if (discovery is null)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.DestinationUnavailable);
        }

        DiscordChannelCapability? channel = discovery.Channels
            .SingleOrDefault(value => value.Id == destination.ChannelId);
        if (channel is null || !channel.CanView || !channel.CanSend)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.MissingPermission);
        }

        if (request.Mode == DiscordMessageMode.Embed && !channel.CanEmbed
            || request.Images.Count > 0 && !channel.CanAttach)
        {
            return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.MissingPermission);
        }

        DiscordMentionBuildResult mentions = DiscordPublishingService.ValidateLiveMentions(
            request,
            channel,
            discovery.Roles);
        foreach (string userId in request.Mentions.UserIds.Distinct(StringComparer.Ordinal))
        {
            if (await discordApi.GetMemberAsync(
                token,
                request.GuildId,
                userId,
                cancellationToken) is null)
            {
                return DeliveryAttemptResult.Permanent(PublicationSafeOutcome.ValidationRejected);
            }
        }

        DiscordMessageRequest message = DiscordPublishingService.BuildMessage(request, mentions) with
        {
            Nonce = stableNonce,
            EnforceNonce = true,
        };
        DiscordApiSendResult sent = await discordApi.SendMessageAsync(
            token,
            destination.ChannelId,
            message,
            request.Images,
            cancellationToken);
        PublicationSafeOutcome outcome = MapStatus(sent.Status);
        return sent.Status == DiscordDeliveryStatus.Success
            ? DeliveryAttemptResult.Success(sent.MessageId)
            : PublicationRetryPolicy.IsTransient(outcome)
                ? DeliveryAttemptResult.Transient(outcome, sent.RetryAfter)
                : DeliveryAttemptResult.Permanent(outcome);
    }

    private async Task PersistResultAsync(
        ClaimedDelivery claim,
        DeliveryAttemptResult result,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        PublicationDelivery? delivery = await dbContext.PublicationDeliveries
            .Include(value => value.Publication)
            .ThenInclude(value => value.Deliveries)
            .Include(value => value.Attempts)
            .SingleOrDefaultAsync(value => value.Id == claim.DeliveryId, cancellationToken);
        if (delivery is null
            || delivery.Status != PublicationDeliveryStatus.Leased
            || delivery.Revision != claim.LeaseRevision
            || !string.Equals(delivery.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal))
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        PublicationAttempt attempt = delivery.Attempts.Single(
            value => value.AttemptNumber == claim.AttemptNumber);
        bool changed;
        AuditEventCode auditCode;
        if (result.Outcome == PublicationSafeOutcome.Success)
        {
            changed = delivery.Complete(
                claim.LeaseOwner,
                claim.LeaseRevision,
                SafeCode(result.Outcome),
                result.ExternalMessageId,
                now);
            auditCode = AuditEventCode.DiscordPublicationChannelSucceeded;
            attempt.Finish(
                SafeCode(result.Outcome),
                now,
                externalMessageId: result.ExternalMessageId,
                diagnosticReference: result.DiagnosticReference);
        }
        else if (delivery.Publication.CancellationRequestedAtUtc is not null)
        {
            changed = delivery.CancelLeased(claim.LeaseOwner, claim.LeaseRevision, now);
            auditCode = AuditEventCode.PublicationDeliveryCancelled;
            attempt.Finish("cancelled", now, diagnosticReference: result.DiagnosticReference);
        }
        else if (result.ShouldRetry && delivery.AttemptCount < PublicationRetryPolicy.MaximumAttempts)
        {
            DateTimeOffset retryAt = now + PublicationRetryPolicy.DelayAfterAttempt(
                delivery.AttemptCount,
                result.RetryAfter);
            changed = delivery.ScheduleRetry(
                claim.LeaseOwner,
                claim.LeaseRevision,
                SafeCode(result.Outcome),
                retryAt);
            auditCode = AuditEventCode.PublicationRetryScheduled;
            attempt.Finish(
                SafeCode(result.Outcome),
                now,
                retryAt,
                diagnosticReference: result.DiagnosticReference);
        }
        else
        {
            changed = delivery.FailPermanent(
                claim.LeaseOwner,
                claim.LeaseRevision,
                SafeCode(result.Outcome),
                now);
            auditCode = AuditEventCode.DiscordPublicationChannelFailed;
            attempt.Finish(
                SafeCode(result.Outcome),
                now,
                diagnosticReference: result.DiagnosticReference);
        }

        if (!changed)
        {
            return;
        }

        delivery.Publication.Recalculate(delivery.Publication.Deliveries.ToArray(), now);
        await auditWriter.WriteAsync(
            new AuditEvent(
                auditCode,
                result.Outcome == PublicationSafeOutcome.Success
                    ? AuditOutcome.Succeeded
                    : AuditOutcome.Failed,
                delivery.Publication.RequestedByUserId),
            cancellationToken);
        if (Publication.IsTerminal(delivery.Publication.Status))
        {
            PublicationPayload? payload = await dbContext.PublicationPayloads
                .SingleOrDefaultAsync(value => value.PublicationId == delivery.PublicationId, cancellationToken);
            if (payload is not null)
            {
                dbContext.PublicationPayloads.Remove(payload);
                await auditWriter.WriteAsync(
                    new AuditEvent(
                        AuditEventCode.PublicationPayloadRemoved,
                        AuditOutcome.Succeeded,
                        delivery.Publication.RequestedByUserId),
                    cancellationToken);
            }

            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.PublicationFinalized,
                    AuditOutcome.Succeeded,
                    delivery.Publication.RequestedByUserId),
                cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static PublicationSafeOutcome MapStatus(DiscordDeliveryStatus status) => status switch
    {
        DiscordDeliveryStatus.Success => PublicationSafeOutcome.Success,
        DiscordDeliveryStatus.RateLimited => PublicationSafeOutcome.RateLimited,
        DiscordDeliveryStatus.MissingPermission => PublicationSafeOutcome.MissingPermission,
        DiscordDeliveryStatus.DestinationUnavailable => PublicationSafeOutcome.DestinationUnavailable,
        DiscordDeliveryStatus.AuthenticationFailed => PublicationSafeOutcome.AuthenticationFailed,
        DiscordDeliveryStatus.ValidationRejected => PublicationSafeOutcome.ValidationRejected,
        DiscordDeliveryStatus.DiscordUnavailable => PublicationSafeOutcome.DiscordUnavailable,
        DiscordDeliveryStatus.TimedOut => PublicationSafeOutcome.TimedOut,
        DiscordDeliveryStatus.Cancelled => PublicationSafeOutcome.TimedOut,
        _ => PublicationSafeOutcome.UnexpectedFailure,
    };

    private static PublicationSafeOutcome MapDiscoveryFailure(
        DiscordServerInformationFailure failure) => failure switch
        {
            DiscordServerInformationFailure.AuthenticationFailed => PublicationSafeOutcome.AuthenticationFailed,
            DiscordServerInformationFailure.NotInstalled => PublicationSafeOutcome.DestinationUnavailable,
            DiscordServerInformationFailure.AccessDenied => PublicationSafeOutcome.MissingPermission,
            DiscordServerInformationFailure.UnsupportedResponse => PublicationSafeOutcome.ValidationRejected,
            _ => PublicationSafeOutcome.DiscordUnavailable,
        };

    private static string SafeCode(PublicationSafeOutcome outcome) => outcome switch
    {
        PublicationSafeOutcome.Success => "success",
        PublicationSafeOutcome.RateLimited => "rate-limited",
        PublicationSafeOutcome.MissingPermission => "missing-permission",
        PublicationSafeOutcome.DestinationUnavailable => "destination-unavailable",
        PublicationSafeOutcome.AuthenticationFailed => "authentication-failed",
        PublicationSafeOutcome.ValidationRejected => "validation-rejected",
        PublicationSafeOutcome.DiscordUnavailable => "discord-unavailable",
        PublicationSafeOutcome.TimedOut => "timed-out",
        PublicationSafeOutcome.Cancelled => "cancelled",
        PublicationSafeOutcome.ConnectionFailure => "connection-failure",
        PublicationSafeOutcome.ProtectedPayloadInvalid => "protected-payload-invalid",
        _ => "unexpected-failure",
    };

    private sealed record ClaimedDelivery(
        Guid DeliveryId,
        Guid PublicationId,
        long LeaseRevision,
        int AttemptNumber,
        string LeaseOwner);

    private sealed record DeliveryAttemptResult(
        PublicationSafeOutcome Outcome,
        bool ShouldRetry,
        string? ExternalMessageId,
        TimeSpan? RetryAfter,
        string? DiagnosticReference)
    {
        internal static DeliveryAttemptResult Success(string? messageId) =>
            new(PublicationSafeOutcome.Success, false, messageId, null, null);

        internal static DeliveryAttemptResult Transient(
            PublicationSafeOutcome outcome,
            TimeSpan? retryAfter = null) => new(outcome, true, null, retryAfter, null);

        internal static DeliveryAttemptResult Permanent(
            PublicationSafeOutcome outcome,
            string? diagnosticReference = null) =>
            new(outcome, false, null, null, diagnosticReference);
    }
}
