using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

internal sealed class DiscordPublishingService(
    CreatorToolkitDbContext dbContext,
    IProtectedSecretValueResolver secretResolver,
    IDiscordApi discordApi,
    IAuditWriter auditWriter,
    DiscordPublishingOptions options) : IDiscordPublishingService
{
    public async Task<DiscordPublishContext?> GetContextAsync(
        Guid announcementId,
        CancellationToken cancellationToken = default)
    {
        AnnouncementDetails? announcement = await dbContext.Announcements
            .AsNoTracking()
            .Where(value => value.Id == announcementId)
            .Select(value => new AnnouncementDetails(
                value.Id,
                value.Title,
                value.Body,
                value.Status,
                value.CreatedAtUtc,
                value.UpdatedAtUtc,
                value.CreatedByUserId,
                value.UpdatedByUserId,
                value.Revision))
            .SingleOrDefaultAsync(cancellationToken);
        if (announcement is null || announcement.Status != AnnouncementStatus.Draft)
        {
            return null;
        }

        DiscordConnectionListItem[] connections = await dbContext.DiscordConnections
            .AsNoTracking()
            .Where(value => value.Enabled && value.Destinations.Any(destination => destination.Enabled))
            .OrderBy(value => value.Name)
            .Select(value => new DiscordConnectionListItem(
                value.Id,
                value.Name,
                value.ApplicationId,
                value.BotUserId,
                value.BotUsernameSnapshot,
                value.Enabled,
                value.Revision,
                value.Destinations.Count(destination => destination.Enabled)))
            .ToArrayAsync(cancellationToken);
        DiscordDestinationListItem[] destinations = await dbContext.DiscordDestinations
            .AsNoTracking()
            .Where(value => value.Enabled && value.Connection.Enabled)
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
        return new DiscordPublishContext(
            announcement.Id,
            announcement.Title,
            announcement.Body,
            announcement.Revision,
            connections,
            destinations);
    }

    public async Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(
        Guid connectionId,
        string guildId,
        string query,
        CancellationToken cancellationToken = default)
    {
        string normalized = query.Trim();
        if (normalized.EnumerateRunes().Count() is < 2 or > 100
            || !DiscordSnowflake.IsValid(guildId))
        {
            return [];
        }

        DiscordConnection? connection = await GetConnectionAsync(connectionId, cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            return [];
        }

        return await UseTokenAsync(
            connection,
            (token, ct) => discordApi.SearchMembersAsync(token, guildId, normalized, ct),
            cancellationToken);
    }

    public async Task<DiscordGuildMember?> ValidateMemberAsync(
        Guid connectionId,
        string guildId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordSnowflake.IsValid(guildId) || !DiscordSnowflake.IsValid(userId))
        {
            return null;
        }

        DiscordConnection? connection = await GetConnectionAsync(connectionId, cancellationToken);
        return connection is null || !connection.Enabled
            ? null
            : await UseTokenAsync(
                connection,
                (token, ct) => discordApi.GetMemberAsync(token, guildId, userId, ct),
                cancellationToken);
    }

    public async Task<DiscordPublicationResult> PublishAsync(
        DiscordPublishRequest request,
        bool canUseMassMentions,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SubmissionId == Guid.Empty)
        {
            throw new DiscordPublicationValidationException(
                "Reload the publication form before sending.");
        }

        if (request.DestinationIds.Count is < 1 or > 10)
        {
            throw new DiscordPublicationValidationException(
                "Select between 1 and 10 Discord channels.");
        }

        Announcement? announcement = await dbContext.Announcements
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == request.AnnouncementId, cancellationToken);
        if (announcement is null
            || announcement.Status != AnnouncementStatus.Draft
            || announcement.Revision != request.AnnouncementRevision)
        {
            throw new DiscordPublicationValidationException(
                "The announcement changed or is no longer a Draft. Review it again before publishing.");
        }

        DiscordConnection? connection = await GetConnectionAsync(
            request.ConnectionId,
            cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            throw new DiscordPublicationValidationException(
                "The selected Discord connection is unavailable.");
        }

        Guid[] selectedIds = request.DestinationIds.Distinct().ToArray();
        if (selectedIds.Length != request.DestinationIds.Count)
        {
            throw new DiscordPublicationValidationException(
                "Each Discord channel may be selected only once.");
        }

        DiscordDestination[] destinations = await dbContext.DiscordDestinations
            .AsNoTracking()
            .Where(value => selectedIds.Contains(value.Id))
            .OrderBy(value => value.ChannelNameSnapshot)
            .ToArrayAsync(cancellationToken);
        if (destinations.Length != selectedIds.Length
            || destinations.Any(value =>
                !value.Enabled
                || value.DiscordConnectionId != connection.Id
                || value.GuildId != request.GuildId))
        {
            throw new DiscordPublicationValidationException(
                "One or more selected Discord destinations are unavailable or belong to another server.");
        }

        await WriteAuditAsync(
            AuditEventCode.DiscordPublicationRequested,
            actorUserId,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        using CancellationTokenSource timeout = new(options.OverallTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            return await UseTokenAsync(
                connection,
                (token, ct) => PublishWithTokenAsync(
                    token,
                    connection,
                    destinations,
                    request,
                    canUseMassMentions,
                    actorUserId,
                    cancellationToken,
                    ct),
                linked.Token);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await CompleteWithoutSendingAsync(
                request.SubmissionId,
                destinations,
                DiscordDeliveryStatus.TimedOut,
                actorUserId);
        }
        catch (DiscordApiAuthenticationException)
        {
            return await CompleteWithoutSendingAsync(
                request.SubmissionId,
                destinations,
                DiscordDeliveryStatus.AuthenticationFailed,
                actorUserId);
        }
        catch (DiscordApiUnavailableException)
        {
            return await CompleteWithoutSendingAsync(
                request.SubmissionId,
                destinations,
                DiscordDeliveryStatus.DiscordUnavailable,
                actorUserId);
        }
    }

    private async Task<DiscordPublicationResult> PublishWithTokenAsync(
        string token,
        DiscordConnection connection,
        IReadOnlyList<DiscordDestination> destinations,
        DiscordPublishRequest request,
        bool canUseMassMentions,
        Guid actorUserId,
        CancellationToken requestCancellationToken,
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
            throw new DiscordPublicationValidationException(
                "The bot is no longer installed in the selected Discord server.");
        }

        Dictionary<string, DiscordChannelCapability> liveChannels = discovery.Channels
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        if (destinations.Any(value => !liveChannels.ContainsKey(value.ChannelId)))
        {
            throw new DiscordPublicationValidationException(
                "One or more selected channels are no longer available to the bot.");
        }

        DiscordMentionBuildResult mentions = ValidateMentions(
            request,
            canUseMassMentions,
            destinations,
            liveChannels,
            discovery.Roles);
        foreach (string userId in request.Mentions.UserIds.Distinct(StringComparer.Ordinal))
        {
            if (await discordApi.GetMemberAsync(
                token,
                request.GuildId,
                DiscordSnowflake.Require(userId),
                cancellationToken) is null)
            {
                throw new DiscordPublicationValidationException(
                    "A selected Discord member is no longer in the server.");
            }
        }

        DiscordMessageRequest message = BuildMessage(request, mentions);
        if (request.Mode == DiscordMessageMode.Embed
            && destinations.Any(value => !liveChannels[value.ChannelId].CanEmbed))
        {
            throw new DiscordPublicationValidationException(
                "The bot cannot embed links in every selected channel.");
        }

        if (request.UploadedImage is not null
            && destinations.Any(value => !liveChannels[value.ChannelId].CanAttach))
        {
            throw new DiscordPublicationValidationException(
                "The bot cannot attach files in every selected channel.");
        }

        bool announcementIsCurrent = await dbContext.Announcements
            .AsNoTracking()
            .AnyAsync(
                value => value.Id == request.AnnouncementId
                    && value.Status == AnnouncementStatus.Draft
                    && value.Revision == request.AnnouncementRevision,
                cancellationToken);
        if (!announcementIsCurrent)
        {
            throw new DiscordPublicationValidationException(
                "The announcement changed or is no longer a Draft. Review it again before publishing.");
        }

        List<DiscordDeliveryResult> results = [];
        bool authenticationFailed = false;
        foreach (DiscordDestination destination in destinations)
        {
            DiscordApiSendResult sent;
            if (authenticationFailed)
            {
                sent = new DiscordApiSendResult(DiscordDeliveryStatus.AuthenticationFailed);
            }
            else
            {
                DiscordMessageRequest channelMessage = message with
                {
                    Nonce = DiscordNonce.Create(request.SubmissionId, destination.ChannelId),
                };
                try
                {
                    sent = await discordApi.SendMessageAsync(
                        token,
                        destination.ChannelId,
                        channelMessage,
                        request.UploadedImage,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    sent = new DiscordApiSendResult(
                        requestCancellationToken.IsCancellationRequested
                            ? DiscordDeliveryStatus.Cancelled
                            : DiscordDeliveryStatus.TimedOut);
                }
                catch (DiscordApiUnavailableException)
                {
                    sent = new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable);
                }
            }

            authenticationFailed |= sent.Status == DiscordDeliveryStatus.AuthenticationFailed;
            results.Add(
                new DiscordDeliveryResult(
                    destination.Id,
                    destination.GuildNameSnapshot,
                    destination.ChannelNameSnapshot,
                    sent.Status,
                    sent.MessageId,
                    CorrectiveAction(sent.Status)));
            await WriteAuditAsync(
                sent.Status == DiscordDeliveryStatus.Success
                    ? AuditEventCode.DiscordPublicationChannelSucceeded
                    : AuditEventCode.DiscordPublicationChannelFailed,
                actorUserId,
                CancellationToken.None,
                sent.Status == DiscordDeliveryStatus.Success
                    ? AuditOutcome.Succeeded
                    : AuditOutcome.Failed);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        return new DiscordPublicationResult(request.SubmissionId, results);
    }

    private static DiscordMentionBuildResult ValidateMentions(
        DiscordPublishRequest request,
        bool canUseMassMentions,
        IReadOnlyList<DiscordDestination> destinations,
        Dictionary<string, DiscordChannelCapability> channels,
        IReadOnlyList<DiscordRole> roles)
    {
        bool mass = request.Mentions.Everyone || request.Mentions.Here;
        if (mass && (!canUseMassMentions || !request.MassMentionConfirmed))
        {
            throw new DiscordPublicationValidationException(
                "Mass mentions require Owner or Admin authorization and explicit confirmation.");
        }

        bool allCanMassMention = destinations.All(
            value => channels[value.ChannelId].CanMentionEveryone);
        if (mass && !allCanMassMention)
        {
            throw new DiscordPublicationValidationException(
                "The bot cannot use mass mentions in every selected channel.");
        }

        Dictionary<string, DiscordRole> byId = roles.ToDictionary(value => value.Id);
        foreach (string roleId in request.Mentions.RoleIds.Distinct(StringComparer.Ordinal))
        {
            DiscordSnowflake.Require(roleId);
            if (roleId == request.GuildId
                || !byId.TryGetValue(roleId, out DiscordRole? role)
                || (!role.Mentionable && (!canUseMassMentions || !allCanMassMention)))
            {
                throw new DiscordPublicationValidationException(
                    "A selected Discord role cannot be mentioned safely.");
            }
        }

        return request.Mentions.Build();
    }

    internal static DiscordMessageRequest BuildMessage(
        DiscordPublishRequest request,
        DiscordMentionBuildResult mentions)
    {
        if (request.UploadedImage is not null && !string.IsNullOrWhiteSpace(request.RemoteImageUrl))
        {
            throw new DiscordPublicationValidationException(
                "Choose either an uploaded image or a remote HTTPS image URL, not both.");
        }

        Uri? remoteImage = DiscordMessageValidation.OptionalHttpsUri(
            request.RemoteImageUrl,
            "Remote image URL");
        string Prefix(string? value)
        {
            string content = value ?? string.Empty;
            return string.IsNullOrEmpty(mentions.VisiblePrefix)
                ? content
                : string.IsNullOrEmpty(content)
                    ? mentions.VisiblePrefix
                    : mentions.VisiblePrefix + "\n" + content;
        }

        IReadOnlyList<DiscordAttachmentPayload>? attachments = request.UploadedImage is null
            ? null
            : [new DiscordAttachmentPayload(
                0,
                request.UploadedImage.OutboundFileName,
                request.UploadedImage.AltText,
                request.UploadedImage.Spoiler)];
        if (request.Mode == DiscordMessageMode.Plain)
        {
            string content = Prefix(request.PlainContent);
            if (remoteImage is not null)
            {
                content = string.IsNullOrWhiteSpace(content)
                    ? remoteImage.AbsoluteUri
                    : content + "\n" + remoteImage.AbsoluteUri;
            }

            DiscordMessageValidation.RequireScalarLimit(
                content,
                DiscordMessageValidation.MaximumMessageLength,
                "Message content");
            if (string.IsNullOrWhiteSpace(content) && request.UploadedImage is null)
            {
                throw new DiscordPublicationValidationException(
                    "Enter message content or select an image.");
            }

            return new DiscordMessageRequest(
                string.IsNullOrWhiteSpace(content) ? null : content,
                null,
                mentions.AllowedMentions,
                request.ShowLinkPreviews ? 0 : 4,
                string.Empty,
                true,
                attachments);
        }

        DiscordEmbedInput embed = request.Embed
            ?? throw new DiscordPublicationValidationException("Enter rich embed content.");
        string messageText = Prefix(embed.MessageText);
        DiscordMessageValidation.RequireScalarLimit(
            messageText,
            DiscordMessageValidation.MaximumMessageLength,
            "Message content");
        DiscordMessageValidation.RequireScalarLimit(
            embed.Title,
            DiscordMessageValidation.MaximumEmbedTitleLength,
            "Embed title");
        DiscordMessageValidation.RequireScalarLimit(
            embed.Description,
            DiscordMessageValidation.MaximumEmbedDescriptionLength,
            "Embed description");
        DiscordMessageValidation.RequireScalarLimit(
            embed.Footer,
            DiscordMessageValidation.MaximumEmbedFooterLength,
            "Embed footer");
        int aggregate = new[] { embed.Title, embed.Description, embed.Footer }
            .Sum(value => (value ?? string.Empty).EnumerateRunes().Count());
        if (aggregate > DiscordMessageValidation.MaximumEmbedAggregateLength)
        {
            throw new DiscordPublicationValidationException(
                "The rich embed exceeds Discord's 6,000-character aggregate limit.");
        }

        Uri? titleUrl = DiscordMessageValidation.OptionalHttpsUri(embed.TitleUrl, "Embed title URL");
        Uri? imageUrl = DiscordMessageValidation.OptionalHttpsUri(embed.ImageUrl, "Embed image URL");
        Uri? thumbnailUrl = DiscordMessageValidation.OptionalHttpsUri(
            embed.ThumbnailUrl,
            "Embed thumbnail URL");
        if (request.UploadedImage?.EmbedPlacement == true)
        {
            imageUrl = new Uri($"attachment://{request.UploadedImage.OutboundFileName}");
        }
        else if (remoteImage is not null)
        {
            imageUrl = remoteImage;
        }

        DiscordEmbedPayload payload = new(
            NullIfEmpty(embed.Title),
            NullIfEmpty(embed.Description),
            titleUrl?.AbsoluteUri,
            DiscordMessageValidation.OptionalColor(embed.Color),
            string.IsNullOrWhiteSpace(embed.Footer)
                ? null
                : new DiscordEmbedFooter(embed.Footer),
            imageUrl is null ? null : new DiscordEmbedMedia(imageUrl.OriginalString),
            thumbnailUrl is null ? null : new DiscordEmbedMedia(thumbnailUrl.AbsoluteUri));
        if (payload.Title is null && payload.Description is null && payload.Image is null)
        {
            throw new DiscordPublicationValidationException(
                "The rich embed needs a title, description, or image.");
        }

        return new DiscordMessageRequest(
            NullIfEmpty(messageText),
            [payload],
            mentions.AllowedMentions,
            0,
            string.Empty,
            true,
            attachments);
    }

    internal static string CorrectiveAction(DiscordDeliveryStatus status) => status switch
    {
        DiscordDeliveryStatus.Success => "Delivered to Discord.",
        DiscordDeliveryStatus.RateLimited => "Wait briefly, then confirm Discord received the message before retrying.",
        DiscordDeliveryStatus.MissingPermission => "Review the bot's permissions for this channel.",
        DiscordDeliveryStatus.DestinationUnavailable => "Refresh or replace this saved channel destination.",
        DiscordDeliveryStatus.AuthenticationFailed => "Replace and validate the Discord bot token.",
        DiscordDeliveryStatus.ValidationRejected => "Review the message fields and Discord limits.",
        DiscordDeliveryStatus.DiscordUnavailable => "Discord is unavailable. Confirm delivery before trying again.",
        DiscordDeliveryStatus.TimedOut => "The request timed out. Confirm delivery in Discord before trying again.",
        DiscordDeliveryStatus.Cancelled => "The request was cancelled. Confirm delivery in Discord before trying again.",
        _ => "Review the destination and confirm delivery before trying again.",
    };

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
            DiscordConfigurationService.SecretPurpose(connection.Id),
            operation,
            cancellationToken);

    private Task WriteAuditAsync(
        AuditEventCode code,
        Guid actorUserId,
        CancellationToken cancellationToken,
        AuditOutcome outcome = AuditOutcome.Succeeded) =>
        auditWriter.WriteAsync(
            new AuditEvent(code, outcome, actorUserId),
            cancellationToken);

    private async Task<DiscordPublicationResult> CompleteWithoutSendingAsync(
        Guid submissionId,
        IReadOnlyList<DiscordDestination> destinations,
        DiscordDeliveryStatus status,
        Guid actorUserId)
    {
        List<DiscordDeliveryResult> results = [];
        foreach (DiscordDestination destination in destinations)
        {
            results.Add(new DiscordDeliveryResult(
                destination.Id,
                destination.GuildNameSnapshot,
                destination.ChannelNameSnapshot,
                status,
                null,
                CorrectiveAction(status)));
            await WriteAuditAsync(
                AuditEventCode.DiscordPublicationChannelFailed,
                actorUserId,
                CancellationToken.None,
                AuditOutcome.Failed);
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
        return new DiscordPublicationResult(submissionId, results);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
