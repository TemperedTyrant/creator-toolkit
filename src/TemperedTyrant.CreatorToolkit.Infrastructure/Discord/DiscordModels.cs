using System.Text;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

public sealed class DiscordConnection
{
    public const int MaximumNameLength = 100;
    public const int MaximumUsernameLength = 100;

    private DiscordConnection()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid ProtectedSecretId { get; private set; }

    public string ApplicationId { get; private set; } = string.Empty;

    public string BotUserId { get; private set; } = string.Empty;

    public string BotUsernameSnapshot { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Revision { get; private set; }

    public ICollection<DiscordDestination> Destinations { get; private set; } = [];

    internal static DiscordConnection Create(
        Guid id,
        string name,
        Guid protectedSecretId,
        DiscordBotIdentity identity,
        DateTimeOffset now)
    {
        return new DiscordConnection
        {
            Id = id,
            Name = ValidateName(name),
            ProtectedSecretId = protectedSecretId,
            ApplicationId = DiscordSnowflake.Require(identity.ApplicationId),
            BotUserId = DiscordSnowflake.Require(identity.BotUserId),
            BotUsernameSnapshot = ValidateSnapshot(identity.BotUsername),
            Enabled = true,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
            Revision = 1,
        };
    }

    internal void ReplaceIdentity(
        DiscordBotIdentity identity,
        long expectedRevision,
        DateTimeOffset now)
    {
        RequireRevision(expectedRevision);
        ApplicationId = DiscordSnowflake.Require(identity.ApplicationId);
        BotUserId = DiscordSnowflake.Require(identity.BotUserId);
        BotUsernameSnapshot = ValidateSnapshot(identity.BotUsername);
        RecordMutation(now);
    }

    internal void SetEnabled(bool enabled, long expectedRevision, DateTimeOffset now)
    {
        RequireRevision(expectedRevision);
        Enabled = enabled;
        RecordMutation(now);
    }

    private static string ValidateName(string value)
    {
        string normalized = value.Trim();
        int scalarCount = normalized.EnumerateRunes().Count();
        if (scalarCount is < 1 or > MaximumNameLength)
        {
            throw new ArgumentException(
                $"The connection name must contain 1 to {MaximumNameLength} Unicode characters.",
                nameof(value));
        }

        return normalized;
    }

    private static string ValidateSnapshot(string value)
    {
        string normalized = value.Trim();
        int scalarCount = normalized.EnumerateRunes().Count();
        return scalarCount is >= 1 and <= MaximumUsernameLength
            ? normalized
            : "Discord bot";
    }

    private void RequireRevision(long expectedRevision)
    {
        if (Revision != expectedRevision)
        {
            throw new DiscordStaleRevisionException();
        }
    }

    private void RecordMutation(DateTimeOffset now)
    {
        UpdatedAtUtc = now.ToUniversalTime();
        Revision = checked(Revision + 1);
    }
}

public sealed class DiscordDestination
{
    private DiscordDestination()
    {
    }

    public Guid Id { get; private set; }

    public Guid DiscordConnectionId { get; private set; }

    public DiscordConnection Connection { get; private set; } = null!;

    public string GuildId { get; private set; } = string.Empty;

    public string GuildNameSnapshot { get; private set; } = string.Empty;

    public string ChannelId { get; private set; } = string.Empty;

    public string ChannelNameSnapshot { get; private set; } = string.Empty;

    public int ChannelType { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Revision { get; private set; }

    internal static DiscordDestination Create(
        Guid id,
        Guid connectionId,
        DiscordGuild guild,
        DiscordChannelCapability channel,
        DateTimeOffset now)
    {
        if (!channel.CanView || !channel.CanSend || channel.Type is not 0 and not 5)
        {
            throw new InvalidOperationException("The Discord channel is not usable.");
        }

        return new DiscordDestination
        {
            Id = id,
            DiscordConnectionId = connectionId,
            GuildId = DiscordSnowflake.Require(guild.Id),
            GuildNameSnapshot = Snapshot(guild.Name, "Discord server"),
            ChannelId = DiscordSnowflake.Require(channel.Id),
            ChannelNameSnapshot = Snapshot(channel.Name, "Discord channel"),
            ChannelType = channel.Type,
            Enabled = true,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
            Revision = 1,
        };
    }

    internal void SetEnabled(bool enabled, long expectedRevision, DateTimeOffset now)
    {
        if (Revision != expectedRevision)
        {
            throw new DiscordStaleRevisionException();
        }

        Enabled = enabled;
        UpdatedAtUtc = now.ToUniversalTime();
        Revision = checked(Revision + 1);
    }

    private static string Snapshot(string value, string fallback)
    {
        string normalized = value.Trim();
        return normalized.EnumerateRunes().Count() is >= 1 and <= 100
            ? normalized
            : fallback;
    }
}

internal sealed class DiscordStaleRevisionException : Exception;
