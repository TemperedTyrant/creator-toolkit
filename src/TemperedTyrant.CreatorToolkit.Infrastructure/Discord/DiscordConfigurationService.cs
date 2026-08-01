using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

internal sealed class DiscordConfigurationService(
    CreatorToolkitDbContext dbContext,
    ISecretStore secretStore,
    IProtectedSecretValueResolver secretResolver,
    IDiscordApi discordApi,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    IDiagnosticRecorder diagnosticRecorder,
    ILogger<DiscordConfigurationService> logger) : IDiscordConfigurationService
{
    private static readonly Action<ILogger, string, Guid, string, string, string, Exception?>
        LogDiscoveryFailure = LoggerMessage.Define<string, Guid, string, string, string>(
            LogLevel.Warning,
            new EventId(4200, "DiscordServerDiscoveryFailed"),
            "Discord server discovery failed. Stage: {OperationStage}; connection: {ConnectionId}; "
            + "guild: {GuildId}; category: {FailureCategory}; reference: {DiagnosticReference}.");

    public async Task<IReadOnlyList<DiscordConnectionListItem>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.DiscordConnections
            .AsNoTracking()
            .OrderBy(value => value.Name)
            .Select(value => new DiscordConnectionListItem(
                value.Id,
                value.Name,
                value.ApplicationId,
                value.BotUserId,
                value.BotUsernameSnapshot,
                value.Enabled,
                value.Revision,
                value.Destinations.Count))
            .ToArrayAsync(cancellationToken);

    public async Task<DiscordConnectionDetails?> GetAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        DiscordConnectionListItem? connection = await dbContext.DiscordConnections
            .AsNoTracking()
            .Where(value => value.Id == connectionId)
            .Select(value => new DiscordConnectionListItem(
                value.Id,
                value.Name,
                value.ApplicationId,
                value.BotUserId,
                value.BotUsernameSnapshot,
                value.Enabled,
                value.Revision,
                value.Destinations.Count))
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            return null;
        }

        DiscordDestinationListItem[] destinations = await dbContext.DiscordDestinations
            .AsNoTracking()
            .Where(value => value.DiscordConnectionId == connectionId)
            .OrderBy(value => value.GuildNameSnapshot)
            .ThenBy(value => value.ChannelNameSnapshot)
            .Select(value => new DiscordDestinationListItem(
                value.Id,
                value.DiscordConnectionId,
                value.GuildId,
                value.GuildNameSnapshot,
                value.ChannelId,
                value.ChannelNameSnapshot,
                value.ChannelType,
                value.Enabled,
                value.Revision))
            .ToArrayAsync(cancellationToken);
        return new DiscordConnectionDetails(
            connection,
            destinations,
            CreateInstallationUri(connection.ApplicationId));
    }

    public async Task<DiscordOperationResult> CreateAsync(
        string name,
        string botToken,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        DiscordBotIdentity identity;
        try
        {
            identity = await discordApi.ValidateBotAsync(botToken, cancellationToken);
        }
        catch (DiscordApiAuthenticationException)
        {
            return AuthenticationFailure(submitted: true);
        }
        catch (DiscordApiUnavailableException)
        {
            return UnavailableFailure();
        }

        Guid id = Guid.NewGuid();
        try
        {
            await using var transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            SecretReference secret = await secretStore.CreateAsync(
                SecretPurpose(id),
                botToken,
                cancellationToken);
            DiscordConnection connection = DiscordConnection.Create(
                id,
                name,
                secret.Id,
                identity,
                timeProvider.GetUtcNow());
            dbContext.DiscordConnections.Add(connection);
            await AuditAsync(AuditEventCode.DiscordConnectionCreated, actorUserId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DiscordOperationResult.Success(id);
        }
        catch (ArgumentException exception)
        {
            return new DiscordOperationResult(
                DiscordOperationStatus.ValidationFailed,
                SafeMessage: exception.Message);
        }
    }

    public async Task<DiscordOperationResult> ReplaceTokenAsync(
        Guid connectionId,
        long expectedRevision,
        string botToken,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        DiscordBotIdentity identity;
        try
        {
            identity = await discordApi.ValidateBotAsync(botToken, cancellationToken);
        }
        catch (DiscordApiAuthenticationException)
        {
            return AuthenticationFailure(submitted: true);
        }
        catch (DiscordApiUnavailableException)
        {
            return UnavailableFailure();
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        DiscordConnection? connection = await dbContext.DiscordConnections
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        try
        {
            connection.ReplaceIdentity(identity, expectedRevision, timeProvider.GetUtcNow());
        }
        catch (DiscordStaleRevisionException)
        {
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }

        try
        {
            await secretStore.ReplaceAsync(
                new SecretReference(connection.ProtectedSecretId),
                botToken,
                cancellationToken);
            await AuditAsync(AuditEventCode.DiscordTokenReplaced, actorUserId, cancellationToken);
            return await SaveAsync(transaction, connectionId, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    public async Task<DiscordOperationResult> RefreshIdentityAsync(
        Guid connectionId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        DiscordConnection? connection = await dbContext.DiscordConnections
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        DiscordBotIdentity identity;
        try
        {
            identity = await UseTokenAsync(
                connection,
                (token, ct) => discordApi.ValidateBotAsync(token, ct),
                cancellationToken);
        }
        catch (DiscordApiAuthenticationException)
        {
            return AuthenticationFailure(submitted: false);
        }
        catch (DiscordApiUnavailableException)
        {
            return UnavailableFailure();
        }

        try
        {
            connection.ReplaceIdentity(identity, expectedRevision, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return DiscordOperationResult.Success(connectionId);
        }
        catch (Exception exception)
            when (exception is DiscordStaleRevisionException or DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    public async Task<DiscordOperationResult> SetConnectionEnabledAsync(
        Guid connectionId,
        long expectedRevision,
        bool enabled,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        DiscordConnection? connection = await dbContext.DiscordConnections
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        try
        {
            connection.SetEnabled(enabled, expectedRevision, timeProvider.GetUtcNow());
            await AuditAsync(
                enabled
                    ? AuditEventCode.DiscordConnectionEnabled
                    : AuditEventCode.DiscordConnectionDisabled,
                actorUserId,
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return DiscordOperationResult.Success(connectionId);
        }
        catch (Exception exception)
            when (exception is DiscordStaleRevisionException or DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    public async Task<DiscordOperationResult> DeleteConnectionAsync(
        Guid connectionId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        DiscordConnection? connection = await dbContext.DiscordConnections
            .Include(value => value.Destinations)
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        if (connection.Revision != expectedRevision)
        {
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }

        try
        {
            dbContext.DiscordConnections.Remove(connection);
            await AuditAsync(AuditEventCode.DiscordConnectionDeleted, actorUserId, cancellationToken);
            await secretStore.DeleteAsync(
                new SecretReference(connection.ProtectedSecretId),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DiscordOperationResult.Success(connectionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    public async Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        DiscordConnection? connection = await GetConnectionAsync(connectionId, cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            return [];
        }

        return await UseTokenAsync(
            connection,
            (token, ct) => discordApi.ListGuildsAsync(token, ct),
            cancellationToken);
    }

    public async Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
        Guid connectionId,
        string guildId,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordSnowflake.IsValid(guildId))
        {
            return null;
        }

        DiscordConnection? connection = await GetConnectionAsync(connectionId, cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            return null;
        }

        try
        {
            return await UseTokenAsync(
                connection,
                (token, ct) => discordApi.DiscoverGuildAsync(
                    token,
                    Identity(connection),
                    guildId,
                    ct),
                cancellationToken);
        }
        catch (DiscordServerInformationException exception)
        {
            throw await RecordDiscoveryFailureAsync(
                connectionId,
                guildId,
                exception.Stage,
                exception.Failure,
                cancellationToken);
        }
        catch (DiscordApiAuthenticationException)
        {
            throw await RecordDiscoveryFailureAsync(
                connectionId,
                guildId,
                DiscordDiscoveryStage.GuildResponse,
                DiscordServerInformationFailure.AuthenticationFailed,
                cancellationToken);
        }
        catch (DiscordApiUnavailableException)
        {
            throw await RecordDiscoveryFailureAsync(
                connectionId,
                guildId,
                DiscordDiscoveryStage.GuildResponse,
                DiscordServerInformationFailure.TemporarilyUnavailable,
                cancellationToken);
        }
    }

    private async Task<DiscordServerInformationException> RecordDiscoveryFailureAsync(
        Guid connectionId,
        string guildId,
        DiscordDiscoveryStage stage,
        DiscordServerInformationFailure failure,
        CancellationToken cancellationToken)
    {
        DiagnosticReference reference = await diagnosticRecorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.Infrastructure,
                DiagnosticOperation.DiscordServerDiscovery,
                ExceptionType(failure)),
            cancellationToken);
        LogDiscoveryFailure(
            logger,
            stage.ToString(),
            connectionId,
            guildId,
            failure.ToString(),
            reference.Value,
            null);
        return new DiscordServerInformationException(stage, failure, reference.Value);
    }

    private static DiagnosticExceptionType ExceptionType(DiscordServerInformationFailure failure) =>
        failure switch
        {
            DiscordServerInformationFailure.TemporarilyUnavailable =>
                DiagnosticExceptionType.InputOutput,
            _ => DiagnosticExceptionType.InvalidOperation,
        };

    public async Task<DiscordOperationResult> SaveDestinationsAsync(
        Guid connectionId,
        string guildId,
        IReadOnlyList<string> channelIds,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (channelIds.Count is < 1 or > 100)
        {
            return new DiscordOperationResult(
                DiscordOperationStatus.ValidationFailed,
                SafeMessage: "Select between 1 and 100 usable channels.");
        }

        DiscordGuildDiscovery? discovery = await DiscoverGuildAsync(
            connectionId,
            guildId,
            cancellationToken);
        if (discovery is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        string[] selected = channelIds.Distinct(StringComparer.Ordinal).ToArray();
        DiscordChannelCapability[] channels = discovery.Channels
            .Where(value => selected.Contains(value.Id, StringComparer.Ordinal))
            .ToArray();
        if (channels.Length != selected.Length)
        {
            return new DiscordOperationResult(
                DiscordOperationStatus.ValidationFailed,
                SafeMessage: "One or more selected channels are no longer usable.");
        }

        HashSet<string> existing = await dbContext.DiscordDestinations
            .Where(value => value.DiscordConnectionId == connectionId)
            .Select(value => value.ChannelId)
            .ToHashSetAsync(cancellationToken);
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (DiscordChannelCapability channel in channels.Where(
            value => !existing.Contains(value.Id)))
        {
            dbContext.DiscordDestinations.Add(
                DiscordDestination.Create(
                    Guid.NewGuid(),
                    connectionId,
                    discovery.Guild,
                    channel,
                    timeProvider.GetUtcNow()));
            await AuditAsync(AuditEventCode.DiscordDestinationAdded, actorUserId, cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DiscordOperationResult.Success(connectionId);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.Duplicate);
        }
    }

    public Task<DiscordOperationResult> SetDestinationEnabledAsync(
        Guid destinationId,
        long expectedRevision,
        bool enabled,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateDestinationAsync(
            destinationId,
            expectedRevision,
            actorUserId,
            destination => destination.SetEnabled(enabled, expectedRevision, timeProvider.GetUtcNow()),
            enabled
                ? AuditEventCode.DiscordDestinationEnabled
                : AuditEventCode.DiscordDestinationDisabled,
            remove: false,
            cancellationToken);

    public Task<DiscordOperationResult> DeleteDestinationAsync(
        Guid destinationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateDestinationAsync(
            destinationId,
            expectedRevision,
            actorUserId,
            _ => { },
            AuditEventCode.DiscordDestinationDeleted,
            remove: true,
            cancellationToken);

    public async Task<DiscordDeliveryResult> SendDestinationTestAsync(
        Guid destinationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        DiscordDestination? destination = await dbContext.DiscordDestinations
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == destinationId, cancellationToken);
        if (destination is null || destination.Revision != expectedRevision || !destination.Enabled)
        {
            return Result(destination, DiscordDeliveryStatus.DestinationUnavailable);
        }

        DiscordConnection? connection = await GetConnectionAsync(
            destination.DiscordConnectionId,
            cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            return Result(destination, DiscordDeliveryStatus.AuthenticationFailed);
        }

        DiscordGuildDiscovery? discovery;
        try
        {
            discovery = await DiscoverGuildAsync(
                connection.Id,
                destination.GuildId,
                cancellationToken);
        }
        catch (DiscordServerInformationException exception)
        {
            DiscordDeliveryStatus status = exception.Failure switch
            {
                DiscordServerInformationFailure.AuthenticationFailed =>
                    DiscordDeliveryStatus.AuthenticationFailed,
                DiscordServerInformationFailure.NotInstalled =>
                    DiscordDeliveryStatus.DestinationUnavailable,
                DiscordServerInformationFailure.AccessDenied =>
                    DiscordDeliveryStatus.MissingPermission,
                _ => DiscordDeliveryStatus.DiscordUnavailable,
            };
            return Result(destination, status);
        }
        catch (DiscordApiAuthenticationException)
        {
            return Result(destination, DiscordDeliveryStatus.AuthenticationFailed);
        }
        catch (DiscordApiUnavailableException)
        {
            return Result(destination, DiscordDeliveryStatus.DiscordUnavailable);
        }
        DiscordChannelCapability? channel = discovery?.Channels.SingleOrDefault(
            value => value.Id == destination.ChannelId);
        if (channel is null)
        {
            return Result(destination, DiscordDeliveryStatus.DestinationUnavailable);
        }

        DiscordMessageRequest request = new(
            "Creator Toolkit destination test",
            null,
            DiscordMentionSelection.None.Build().AllowedMentions,
            0,
            DiscordNonce.Create(Guid.NewGuid(), destination.ChannelId),
            true,
            null);
        DiscordApiSendResult sent;
        try
        {
            sent = await UseTokenAsync(
                connection,
                (token, ct) => discordApi.SendMessageAsync(
                    token,
                    destination.ChannelId,
                    request,
                    null,
                    ct),
                cancellationToken);
        }
        catch (DiscordApiAuthenticationException)
        {
            sent = new DiscordApiSendResult(DiscordDeliveryStatus.AuthenticationFailed);
        }
        catch (DiscordApiUnavailableException)
        {
            sent = new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable);
        }
        await AuditAsync(
            AuditEventCode.DiscordDestinationTestSent,
            actorUserId,
            cancellationToken,
            sent.Status == DiscordDeliveryStatus.Success
                ? AuditOutcome.Succeeded
                : AuditOutcome.Failed);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(destination, sent.Status, sent.MessageId);
    }

    private async Task<DiscordOperationResult> MutateDestinationAsync(
        Guid destinationId,
        long expectedRevision,
        Guid actorUserId,
        Action<DiscordDestination> mutation,
        AuditEventCode eventCode,
        bool remove,
        CancellationToken cancellationToken)
    {
        DiscordDestination? destination = await dbContext.DiscordDestinations
            .SingleOrDefaultAsync(value => value.Id == destinationId, cancellationToken);
        if (destination is null)
        {
            return new DiscordOperationResult(DiscordOperationStatus.NotFound);
        }

        if (destination.Revision != expectedRevision)
        {
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }

        try
        {
            mutation(destination);
            if (remove)
            {
                dbContext.DiscordDestinations.Remove(destination);
            }

            await AuditAsync(eventCode, actorUserId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return DiscordOperationResult.Success(destinationId);
        }
        catch (Exception exception)
            when (exception is DiscordStaleRevisionException or DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    private Task<DiscordConnection?> GetConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken) =>
        dbContext.DiscordConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken);

    private Task<T> UseTokenAsync<T>(
        DiscordConnection connection,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        secretResolver.UseAsync(
            new SecretReference(connection.ProtectedSecretId),
            SecretPurpose(connection.Id),
            operation,
            cancellationToken);

    private static DiscordBotIdentity Identity(DiscordConnection connection) =>
        new(connection.BotUserId, connection.BotUsernameSnapshot, connection.ApplicationId);

    internal static string SecretPurpose(Guid connectionId) =>
        $"discord.bot-token:{connectionId:N}";

    internal static Uri CreateInstallationUri(string applicationId) =>
        new(
            $"https://discord.com/oauth2/authorize?client_id={applicationId}"
            + $"&scope=bot&permissions={DiscordPermissionCalculator.StandardInstallPermissions}");

    private Task AuditAsync(
        AuditEventCode code,
        Guid actorUserId,
        CancellationToken cancellationToken,
        AuditOutcome outcome = AuditOutcome.Succeeded) =>
        auditWriter.WriteAsync(
            new AuditEvent(code, outcome, actorUserId),
            cancellationToken);

    private async Task<DiscordOperationResult> SaveAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DiscordOperationResult.Success(id);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return new DiscordOperationResult(DiscordOperationStatus.StaleRevision);
        }
    }

    private static DiscordOperationResult AuthenticationFailure(bool submitted) =>
        new(
            DiscordOperationStatus.AuthenticationFailed,
            SafeMessage: submitted
                ? "Discord rejected the bot credential."
                : "Discord rejected the configured bot credential.");

    private static DiscordOperationResult UnavailableFailure() =>
        new(
            DiscordOperationStatus.DiscordUnavailable,
            SafeMessage: "Discord could not be reached safely. Try again later.");

    private static DiscordDeliveryResult Result(
        DiscordDestination? destination,
        DiscordDeliveryStatus status,
        string? messageId = null) =>
        new(
            destination?.Id,
            destination?.GuildNameSnapshot ?? "Discord server",
            destination?.ChannelNameSnapshot ?? "Discord channel",
            status,
            messageId,
            DiscordPublishingService.CorrectiveAction(status));
}
