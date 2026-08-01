using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

internal sealed class DiscordPublishingService(
    CreatorToolkitDbContext dbContext,
    IProtectedSecretValueResolver secretResolver,
    IDiscordApi discordApi,
    IAuditWriter auditWriter,
    PublicationPayloadProtector payloadProtector,
    AnnouncementMediaProtector mediaProtector,
    TimeProvider timeProvider) : IDiscordPublishingService
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
                value.MessageContent,
                value.Status,
                value.CreatedAtUtc,
                value.UpdatedAtUtc,
                value.CreatedByUserId,
                value.UpdatedByUserId,
                value.Revision,
                value.Media.OrderBy(media => media.SortOrder).Select(media => new AnnouncementMediaSummary(
                    media.Id,
                    media.SortOrder,
                    media.ContentType,
                    media.ByteLength,
                    media.GeneratedFileName,
                    media.AltText,
                    media.IsSpoiler,
                    media.Presentation,
                    media.Revision)).ToArray()))
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
            announcement.MessageContent,
            announcement.Revision,
            connections,
            destinations,
            announcement.Media);
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

    public async Task ValidateReviewAsync(
        DiscordPublishRequest request,
        bool canUseMassMentions,
        DiscordGuildDiscovery discovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(discovery);
        ValidateRequestShape(request, canUseMassMentions);
        DiscordPublishRequest hydrated = await HydrateStoredMediaAsync(request, cancellationToken);
        try
        {
            _ = BuildMessage(hydrated, hydrated.Mentions.Build());
        }
        finally
        {
            ZeroHydratedImages(request, hydrated);
        }
        if (!string.Equals(discovery.Guild.Id, request.GuildId, StringComparison.Ordinal))
        {
            throw new DiscordPublicationValidationException(
                "Reload the live Discord server information before reviewing this publication.");
        }

        Guid[] selectedIds = request.DestinationIds.Distinct().ToArray();
        DiscordDestination[] destinations = await dbContext.DiscordDestinations.AsNoTracking()
            .Where(value => selectedIds.Contains(value.Id))
            .ToArrayAsync(cancellationToken);
        if (destinations.Length != selectedIds.Length
            || destinations.Any(value => !value.Enabled
                || value.DiscordConnectionId != request.ConnectionId
                || value.GuildId != request.GuildId))
        {
            throw new DiscordPublicationValidationException(
                "One or more selected Discord destinations are unavailable or belong to another server.");
        }

        Dictionary<string, DiscordChannelCapability> liveChannels = discovery.Channels
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (DiscordDestination destination in destinations)
        {
            if (!liveChannels.TryGetValue(destination.ChannelId, out DiscordChannelCapability? channel)
                || !channel.CanView
                || !channel.CanSend)
            {
                throw new DiscordPublicationValidationException(
                    "The bot can no longer send to every selected Discord channel.");
            }

            if (request.Mode == DiscordMessageMode.Embed && !channel.CanEmbed
                || hydrated.Images.Count > 0 && !channel.CanAttach)
            {
                throw new DiscordPublicationValidationException(
                    "The selected Discord channels no longer support the reviewed message.");
            }

            _ = ValidateLiveMentions(hydrated, channel, discovery.Roles);
        }

        DiscordConnection? connection = await GetConnectionAsync(
            request.ConnectionId,
            cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            throw new DiscordPublicationValidationException(
                "The selected Discord connection is unavailable.");
        }

        foreach (string userId in request.Mentions.UserIds.Distinct(StringComparer.Ordinal))
        {
            DiscordGuildMember? member = await UseTokenAsync(
                connection,
                (token, ct) => discordApi.GetMemberAsync(token, request.GuildId, userId, ct),
                cancellationToken);
            if (member is null)
            {
                throw new DiscordPublicationValidationException(
                    "A selected Discord member is no longer in the server.");
            }
        }
    }

    public async Task<DiscordPublicationEnqueueResult> EnqueueAsync(
        DiscordPublishRequest request,
        bool canUseMassMentions,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestShape(request, canUseMassMentions);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        Guid? existing = await dbContext.Publications.AsNoTracking()
            .Where(value => value.SubmissionId == request.SubmissionId
                && value.RequestedByUserId == actorUserId)
            .Select(value => (Guid?)value.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return new DiscordPublicationEnqueueResult(existing.Value, true);
        }

        Announcement? announcement = await dbContext.Announcements.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == request.AnnouncementId, cancellationToken);
        if (announcement is null
            || announcement.Status != AnnouncementStatus.Draft
            || announcement.Revision != request.AnnouncementRevision)
        {
            throw new DiscordPublicationValidationException(
                "The announcement changed or is no longer a Draft. Review it again before publishing.");
        }

        DiscordConnection? connection = await GetConnectionAsync(request.ConnectionId, cancellationToken);
        if (connection is null || !connection.Enabled)
        {
            throw new DiscordPublicationValidationException(
                "The selected Discord connection is unavailable.");
        }

        Guid[] selectedIds = request.DestinationIds.Distinct().ToArray();
        DiscordDestination[] destinations = await dbContext.DiscordDestinations.AsNoTracking()
            .Where(value => selectedIds.Contains(value.Id))
            .OrderBy(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (destinations.Length != selectedIds.Length
            || destinations.Any(value => !value.Enabled
                || value.DiscordConnectionId != request.ConnectionId
                || value.GuildId != request.GuildId))
        {
            throw new DiscordPublicationValidationException(
                "One or more selected Discord destinations are unavailable or belong to another server.");
        }

        DiscordPublishRequest snapshot = await HydrateStoredMediaAsync(request, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid publicationId = Guid.NewGuid();
        PublicationPayload protectedPayload;
        try
        {
            _ = BuildMessage(snapshot, snapshot.Mentions.Build());
            Publication publication = Publication.Create(
                publicationId,
                request.AnnouncementId,
                request.AnnouncementRevision,
                request.SubmissionId,
                actorUserId,
                destinations.Length,
                now);
            dbContext.Publications.Add(publication);
            foreach (DiscordDestination destination in destinations)
            {
                dbContext.PublicationDeliveries.Add(PublicationDelivery.Create(
                    Guid.NewGuid(),
                    publicationId,
                    destination.Id,
                    destination.ChannelId,
                    destination.GuildNameSnapshot,
                    destination.ChannelNameSnapshot,
                    DiscordNonce.Create(request.SubmissionId, destination.ChannelId),
                    now));
            }

            protectedPayload = payloadProtector.Protect(publicationId, snapshot, now);
        }
        finally
        {
            ZeroHydratedImages(request, snapshot);
        }

        dbContext.PublicationPayloads.Add(protectedPayload);
        await auditWriter.WriteAsync(
            new AuditEvent(AuditEventCode.PublicationQueued, AuditOutcome.Succeeded, actorUserId),
            cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordPublicationEnqueueResult(publicationId, false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            Guid? duplicate = await dbContext.Publications.AsNoTracking()
                .Where(value => value.SubmissionId == request.SubmissionId
                    && value.RequestedByUserId == actorUserId)
                .Select(value => (Guid?)value.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                return new DiscordPublicationEnqueueResult(duplicate.Value, true);
            }

            throw;
        }
    }

    public Task<Guid?> FindEnqueuedAsync(
        Guid submissionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        dbContext.Publications.AsNoTracking()
            .Where(value => value.SubmissionId == submissionId
                && value.RequestedByUserId == actorUserId)
            .Select(value => (Guid?)value.Id)
            .SingleOrDefaultAsync(cancellationToken);

    internal static void ValidateRequestShape(
        DiscordPublishRequest request,
        bool canUseMassMentions)
    {
        if (request.SubmissionId == Guid.Empty)
        {
            throw new DiscordPublicationValidationException(
                "Reload the publication form before sending.");
        }

        Guid[] destinations = request.DestinationIds.Distinct().ToArray();
        if (destinations.Length is < 1 or > 10 || destinations.Length != request.DestinationIds.Count)
        {
            throw new DiscordPublicationValidationException(
                "Select between 1 and 10 distinct Discord channels.");
        }

        if ((request.Mentions.Everyone || request.Mentions.Here)
            && (!canUseMassMentions || !request.MassMentionConfirmed))
        {
            throw new DiscordPublicationValidationException(
                "Mass mentions require Owner or Admin authorization and explicit confirmation.");
        }

        if (request.Mentions.RoleIds.Distinct(StringComparer.Ordinal).Count() > 25
            || request.Mentions.UserIds.Distinct(StringComparer.Ordinal).Count() > 25)
        {
            throw new DiscordPublicationValidationException(
                "Select no more than 25 Discord roles and 25 Discord users.");
        }

        foreach (string snowflake in request.Mentions.RoleIds.Concat(request.Mentions.UserIds))
        {
            DiscordSnowflake.Require(snowflake);
        }

        if (request.Images.Count > AnnouncementMediaAsset.MaximumAssetCount)
        {
            throw new DiscordPublicationValidationException("Select no more than four images.");
        }
    }

    private async Task<DiscordPublishRequest> HydrateStoredMediaAsync(
        DiscordPublishRequest request,
        CancellationToken cancellationToken)
    {
        Guid[] requestedIds = request.AnnouncementMediaIds?.Distinct().ToArray() ?? [];
        if (requestedIds.Length == 0)
        {
            return request;
        }

        if (requestedIds.Length > AnnouncementMediaAsset.MaximumAssetCount
            || requestedIds.Length != request.AnnouncementMediaIds!.Count
            || request.UploadedImage is not null
            || request.StoredImages is { Count: > 0 })
        {
            throw new DiscordPublicationValidationException(
                "The selected announcement images are invalid.");
        }

        AnnouncementMediaAsset[] media = await dbContext.AnnouncementMediaAssets
            .AsNoTracking()
            .Where(value => requestedIds.Contains(value.Id)
                && value.AnnouncementId == request.AnnouncementId)
            .OrderBy(value => value.SortOrder)
            .ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (media.Length != requestedIds.Length)
        {
            throw new DiscordPublicationValidationException(
                "One or more selected announcement images are unavailable.");
        }

        if (media.Sum(value => (long)value.ByteLength) > AnnouncementMediaAsset.MaximumCombinedBytes
            || media.Count(value => value.Presentation == AnnouncementMediaPresentation.FeaturedImage) > 1)
        {
            throw new DiscordPublicationValidationException(
                "The selected announcement images are invalid.");
        }

        var images = new List<DiscordValidatedImage>(media.Length);
        try
        {
            foreach (AnnouncementMediaAsset asset in media)
            {
                images.Add(new DiscordValidatedImage(
                    mediaProtector.Unprotect(asset),
                    asset.GeneratedFileName,
                    asset.ContentType,
                    asset.AltText,
                    asset.IsSpoiler,
                    asset.Presentation == AnnouncementMediaPresentation.FeaturedImage));
            }

            return request with
            {
                UploadedImage = null,
                StoredImages = images,
                AnnouncementMediaIds = [],
            };
        }
        catch (AnnouncementMediaUnavailableException)
        {
            foreach (DiscordValidatedImage image in images)
            {
                CryptographicOperations.ZeroMemory(image.Bytes);
            }

            throw new DiscordPublicationValidationException(
                "A selected announcement image could not be read safely.");
        }
    }

    private static void ZeroHydratedImages(
        DiscordPublishRequest original,
        DiscordPublishRequest hydrated)
    {
        if (ReferenceEquals(original, hydrated))
        {
            return;
        }

        foreach (DiscordValidatedImage image in hydrated.Images)
        {
            CryptographicOperations.ZeroMemory(image.Bytes);
        }
    }

    internal static DiscordMentionBuildResult ValidateLiveMentions(
        DiscordPublishRequest request,
        DiscordChannelCapability channel,
        IReadOnlyList<DiscordRole> roles)
    {
        bool mass = request.Mentions.Everyone || request.Mentions.Here;
        if (mass && (!request.MassMentionConfirmed || !channel.CanMentionEveryone))
        {
            throw new DiscordPublicationValidationException(
                "The bot cannot use the reviewed mass mention in this channel.");
        }

        Dictionary<string, DiscordRole> byId = roles.ToDictionary(value => value.Id);
        foreach (string roleId in request.Mentions.RoleIds.Distinct(StringComparer.Ordinal))
        {
            if (roleId == request.GuildId
                || !byId.TryGetValue(roleId, out DiscordRole? role)
                || (!role.Mentionable && !channel.CanMentionEveryone))
            {
                throw new DiscordPublicationValidationException(
                    "A reviewed Discord role can no longer be mentioned safely.");
            }
        }

        return request.Mentions.Build();
    }

    internal static DiscordMessageRequest BuildMessage(
        DiscordPublishRequest request,
        DiscordMentionBuildResult mentions)
    {
        if (request.Images.Count > 0 && !string.IsNullOrWhiteSpace(request.RemoteImageUrl))
        {
            throw new DiscordPublicationValidationException(
                "Choose either an uploaded image or a remote HTTPS image URL, not both.");
        }

        if (request.Images.Count(value => value.EmbedPlacement) > 1)
        {
            throw new DiscordPublicationValidationException(
                "Only one image can be the featured image.");
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

        IReadOnlyList<DiscordAttachmentPayload>? attachments = request.Images.Count == 0
            ? null
            : request.Images.Select((image, index) => new DiscordAttachmentPayload(
                index,
                image.OutboundFileName,
                image.AltText,
                image.Spoiler)).ToArray();
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
            if (string.IsNullOrWhiteSpace(content) && request.Images.Count == 0)
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
        Uri? thumbnailUrl = DiscordMessageValidation.OptionalHttpsUri(embed.ThumbnailUrl, "Embed thumbnail URL");
        DiscordValidatedImage? featured = request.Images.SingleOrDefault(value => value.EmbedPlacement);
        if (featured is not null)
        {
            imageUrl = new Uri($"attachment://{featured.OutboundFileName}");
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
            string.IsNullOrWhiteSpace(embed.Footer) ? null : new DiscordEmbedFooter(embed.Footer),
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
        dbContext.DiscordConnections.AsNoTracking()
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
