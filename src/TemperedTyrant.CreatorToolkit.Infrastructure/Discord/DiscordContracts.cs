using System.Text.Json.Serialization;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

public sealed record DiscordBotIdentity(
    string BotUserId,
    string BotUsername,
    string ApplicationId);

public sealed record DiscordGuild(string Id, string Name, string? IconHash = null);

public sealed record DiscordRole(
    string Id,
    string Name,
    string Permissions,
    bool Mentionable);

public sealed record DiscordGuildMember(
    string UserId,
    string DisplayName,
    IReadOnlyList<string> RoleIds);

public sealed record DiscordPermissionOverwrite(
    string Id,
    int Type,
    string Allow,
    string Deny);

public sealed record DiscordChannel(
    string Id,
    string GuildId,
    string Name,
    int Type,
    IReadOnlyList<DiscordPermissionOverwrite> Overwrites);

public sealed record DiscordChannelCapability(
    string Id,
    string Name,
    int Type,
    bool CanView,
    bool CanSend,
    bool CanEmbed,
    bool CanAttach,
    bool CanMentionEveryone)
{
    internal static DiscordChannelCapability Unusable(DiscordChannel channel) =>
        new(channel.Id, channel.Name, channel.Type, false, false, false, false, false);
}

public sealed record DiscordGuildDiscovery(
    DiscordGuild Guild,
    IReadOnlyList<DiscordChannelCapability> Channels,
    IReadOnlyList<DiscordRole> Roles,
    DiscordGuildMember BotMember);

public sealed record DiscordConnectionListItem(
    Guid Id,
    string Name,
    string ApplicationId,
    string BotUserId,
    string BotUsername,
    bool Enabled,
    long Revision,
    int DestinationCount);

public sealed record DiscordDestinationListItem(
    Guid Id,
    Guid ConnectionId,
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    int ChannelType,
    bool Enabled,
    long Revision);

public sealed record DiscordConnectionDetails(
    DiscordConnectionListItem Connection,
    IReadOnlyList<DiscordDestinationListItem> Destinations,
    Uri InstallationUri);

public enum DiscordOperationStatus
{
    Succeeded = 1,
    NotFound = 2,
    StaleRevision = 3,
    ValidationFailed = 4,
    AuthenticationFailed = 5,
    DiscordUnavailable = 6,
    Duplicate = 7,
}

public sealed record DiscordOperationResult(
    DiscordOperationStatus Status,
    Guid? Id = null,
    string? SafeMessage = null)
{
    public static DiscordOperationResult Success(Guid? id = null) =>
        new(DiscordOperationStatus.Succeeded, id);
}

public interface IDiscordConfigurationService
{
    Task<IReadOnlyList<DiscordConnectionListItem>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<DiscordConnectionDetails?> GetAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> CreateAsync(
        string name,
        string botToken,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> ReplaceTokenAsync(
        Guid connectionId,
        long expectedRevision,
        string botToken,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> RefreshIdentityAsync(
        Guid connectionId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> SetConnectionEnabledAsync(
        Guid connectionId,
        long expectedRevision,
        bool enabled,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> DeleteConnectionAsync(
        Guid connectionId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
        Guid connectionId,
        string guildId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> SaveDestinationsAsync(
        Guid connectionId,
        string guildId,
        IReadOnlyList<string> channelIds,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> SetDestinationEnabledAsync(
        Guid destinationId,
        long expectedRevision,
        bool enabled,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordOperationResult> DeleteDestinationAsync(
        Guid destinationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordDeliveryResult> SendDestinationTestAsync(
        Guid destinationId,
        long expectedRevision,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public enum DiscordMessageMode
{
    Plain = 1,
    Embed = 2,
}

public sealed record DiscordEmbedInput(
    string? MessageText,
    string? Title,
    string? Description,
    string? TitleUrl,
    string? Color,
    string? Footer,
    string? ImageUrl,
    string? ThumbnailUrl);

public sealed record DiscordValidatedImage(
    byte[] Bytes,
    string OutboundFileName,
    string ContentType,
    string? AltText,
    bool Spoiler,
    bool EmbedPlacement);

public sealed record DiscordPublishRequest(
    Guid SubmissionId,
    Guid AnnouncementId,
    long AnnouncementRevision,
    Guid ConnectionId,
    string GuildId,
    IReadOnlyList<Guid> DestinationIds,
    DiscordMessageMode Mode,
    string? PlainContent,
    bool ShowLinkPreviews,
    DiscordEmbedInput? Embed,
    DiscordMentionSelection Mentions,
    bool MassMentionConfirmed,
    string? RemoteImageUrl,
    DiscordValidatedImage? UploadedImage);

public enum DiscordDeliveryStatus
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
}

public sealed record DiscordDeliveryResult(
    Guid? DestinationId,
    string GuildName,
    string ChannelName,
    DiscordDeliveryStatus Status,
    string? DiscordMessageId,
    string CorrectiveAction);

public sealed record DiscordPublicationResult(
    Guid SubmissionId,
    IReadOnlyList<DiscordDeliveryResult> Channels);

public interface IDiscordPublishingService
{
    Task<DiscordPublishContext?> GetContextAsync(
        Guid announcementId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(
        Guid connectionId,
        string guildId,
        string query,
        CancellationToken cancellationToken = default);

    Task<DiscordGuildMember?> ValidateMemberAsync(
        Guid connectionId,
        string guildId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<DiscordPublicationResult> PublishAsync(
        DiscordPublishRequest request,
        bool canUseMassMentions,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordPublicationValidationException(string message) : Exception(message);

public sealed record DiscordPublishContext(
    Guid AnnouncementId,
    string AnnouncementTitle,
    string AnnouncementBody,
    long AnnouncementRevision,
    IReadOnlyList<DiscordConnectionListItem> Connections,
    IReadOnlyList<DiscordDestinationListItem> Destinations);

internal sealed record DiscordMessageRequest(
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content,
    [property: JsonPropertyName("embeds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DiscordEmbedPayload>? Embeds,
    [property: JsonPropertyName("allowed_mentions")]
    DiscordAllowedMentions AllowedMentions,
    [property: JsonPropertyName("flags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int Flags,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("enforce_nonce")] bool EnforceNonce,
    [property: JsonPropertyName("attachments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DiscordAttachmentPayload>? Attachments);

internal sealed record DiscordEmbedPayload(
    [property: JsonPropertyName("title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Title,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    [property: JsonPropertyName("url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Url,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Color,
    [property: JsonPropertyName("footer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DiscordEmbedFooter? Footer,
    [property: JsonPropertyName("image")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DiscordEmbedMedia? Image,
    [property: JsonPropertyName("thumbnail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DiscordEmbedMedia? Thumbnail);

internal sealed record DiscordEmbedFooter([property: JsonPropertyName("text")] string Text);

internal sealed record DiscordEmbedMedia([property: JsonPropertyName("url")] string Url);

internal sealed record DiscordAttachmentPayload(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    [property: JsonPropertyName("is_spoiler")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool IsSpoiler);

internal interface IDiscordApi
{
    Task<DiscordBotIdentity> ValidateBotAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(string token, CancellationToken cancellationToken);

    Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
        string token,
        DiscordBotIdentity identity,
        string guildId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(
        string token,
        string guildId,
        string query,
        CancellationToken cancellationToken);

    Task<DiscordGuildMember?> GetMemberAsync(
        string token,
        string guildId,
        string userId,
        CancellationToken cancellationToken);

    Task<DiscordApiSendResult> SendMessageAsync(
        string token,
        string channelId,
        DiscordMessageRequest request,
        DiscordValidatedImage? image,
        CancellationToken cancellationToken);
}

internal sealed record DiscordApiSendResult(
    DiscordDeliveryStatus Status,
    string? MessageId = null,
    TimeSpan? RetryAfter = null);

internal sealed record DiscordPublishingOptions(TimeSpan OverallTimeout)
{
    internal static DiscordPublishingOptions Default { get; } =
        new(TimeSpan.FromSeconds(30));
}

public sealed class DiscordApiAuthenticationException : Exception;

public sealed class DiscordApiUnavailableException : Exception;
