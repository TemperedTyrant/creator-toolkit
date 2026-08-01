using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

public static class DiscordSnowflake
{
    public static bool IsValid(string? value) =>
        value is not null
        && value.Length is >= 1 and <= 20
        && value.All(char.IsAsciiDigit)
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
        && id > 0;

    public static string Require(string? value) =>
        IsValid(value)
            ? value!
            : throw new ArgumentException("The Discord identifier is invalid.", nameof(value));
}

[Flags]
public enum DiscordPermissions : ulong
{
    None = 0,
    Administrator = 1UL << 3,
    ViewChannel = 1UL << 10,
    SendMessages = 1UL << 11,
    EmbedLinks = 1UL << 14,
    AttachFiles = 1UL << 15,
    MentionEveryone = 1UL << 17,
}

public static class DiscordPermissionCalculator
{
    public const ulong StandardInstallPermissions =
        (ulong)(DiscordPermissions.ViewChannel
            | DiscordPermissions.SendMessages
            | DiscordPermissions.EmbedLinks
            | DiscordPermissions.AttachFiles);

    public static DiscordChannelCapability Calculate(
        DiscordGuild guild,
        DiscordGuildMember bot,
        DiscordChannel channel,
        IReadOnlyList<DiscordRole> roles)
    {
        ArgumentNullException.ThrowIfNull(guild);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(roles);

        Dictionary<string, DiscordRole> byId = roles.ToDictionary(value => value.Id);
        if (!byId.TryGetValue(guild.Id, out DiscordRole? everyone))
        {
            return DiscordChannelCapability.Unusable(channel);
        }

        BigInteger permissions = ParsePermission(everyone.Permissions);
        foreach (string roleId in bot.RoleIds)
        {
            if (byId.TryGetValue(roleId, out DiscordRole? role))
            {
                permissions |= ParsePermission(role.Permissions);
            }
        }

        BigInteger administrator = (ulong)DiscordPermissions.Administrator;
        if ((permissions & administrator) != administrator)
        {
            ApplyOverwrite(ref permissions, channel.Overwrites.FirstOrDefault(
                value => value.Type == 0 && value.Id == guild.Id));

            BigInteger roleAllow = BigInteger.Zero;
            BigInteger roleDeny = BigInteger.Zero;
            foreach (DiscordPermissionOverwrite overwrite in channel.Overwrites.Where(
                value => value.Type == 0 && bot.RoleIds.Contains(value.Id, StringComparer.Ordinal)))
            {
                roleAllow |= ParsePermission(overwrite.Allow);
                roleDeny |= ParsePermission(overwrite.Deny);
            }

            permissions &= ~roleDeny;
            permissions |= roleAllow;
            ApplyOverwrite(ref permissions, channel.Overwrites.FirstOrDefault(
                value => value.Type == 1 && value.Id == bot.UserId));
        }
        else
        {
            permissions = (BigInteger.One << 63) - 1;
        }

        bool canView = Has(permissions, DiscordPermissions.ViewChannel);
        bool canSend = canView && Has(permissions, DiscordPermissions.SendMessages);
        return new DiscordChannelCapability(
            channel.Id,
            channel.Name,
            channel.Type,
            canView,
            canSend,
            canSend && Has(permissions, DiscordPermissions.EmbedLinks),
            canSend && Has(permissions, DiscordPermissions.AttachFiles),
            canSend && Has(permissions, DiscordPermissions.MentionEveryone));
    }

    private static void ApplyOverwrite(
        ref BigInteger permissions,
        DiscordPermissionOverwrite? overwrite)
    {
        if (overwrite is null)
        {
            return;
        }

        permissions &= ~ParsePermission(overwrite.Deny);
        permissions |= ParsePermission(overwrite.Allow);
    }

    private static BigInteger ParsePermission(string value) =>
        BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger result)
            && result >= BigInteger.Zero
                ? result
                : BigInteger.Zero;

    private static bool Has(BigInteger permissions, DiscordPermissions permission)
    {
        BigInteger flag = (ulong)permission;
        return (permissions & flag) == flag;
    }
}

public static class DiscordNonce
{
    public static string Create(Guid submissionId, string channelId)
    {
        DiscordSnowflake.Require(channelId);
        Span<byte> input = stackalloc byte[16 + sizeof(ulong)];
        submissionId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(
            input[16..],
            ulong.Parse(channelId, CultureInfo.InvariantCulture));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }
}

public sealed record DiscordAllowedMentions(
    [property: JsonPropertyName("parse")] IReadOnlyList<string> Parse,
    [property: JsonPropertyName("roles")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Roles,
    [property: JsonPropertyName("users")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Users,
    [property: JsonPropertyName("replied_user")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool RepliedUser = false);

public sealed record DiscordMentionSelection(
    bool Everyone,
    bool Here,
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> UserIds)
{
    public static DiscordMentionSelection None { get; } = new(false, false, [], []);

    public DiscordMentionBuildResult Build()
    {
        if (RoleIds.Count > 25 || UserIds.Count > 25)
        {
            throw new DiscordMessageValidationException(
                "Select no more than 25 roles and 25 users.");
        }

        string[] roles = RoleIds.Distinct(StringComparer.Ordinal).Select(DiscordSnowflake.Require).ToArray();
        string[] users = UserIds.Distinct(StringComparer.Ordinal).Select(DiscordSnowflake.Require).ToArray();
        List<string> visible = [];
        if (Everyone)
        {
            visible.Add("@everyone");
        }

        if (Here)
        {
            visible.Add("@here");
        }

        visible.AddRange(roles.Select(value => $"<@&{value}>"));
        visible.AddRange(users.Select(value => $"<@{value}>"));
        return new DiscordMentionBuildResult(
            string.Join(' ', visible),
            new DiscordAllowedMentions(
                Everyone || Here ? ["everyone"] : [],
                roles.Length == 0 ? null : roles,
                users.Length == 0 ? null : users));
    }
}

public sealed record DiscordMentionBuildResult(
    string VisiblePrefix,
    DiscordAllowedMentions AllowedMentions);

public static class DiscordMessageValidation
{
    public const int MaximumMessageLength = 2_000;
    public const int MaximumEmbedTitleLength = 256;
    public const int MaximumEmbedDescriptionLength = 4_096;
    public const int MaximumEmbedFooterLength = 2_048;
    public const int MaximumEmbedAggregateLength = 6_000;
    public const int MaximumUrlLength = 2_048;

    public static Uri? OptionalHttpsUri(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > MaximumUrlLength
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new DiscordMessageValidationException(
                $"{field} must be an absolute HTTPS URL without credentials.");
        }

        return uri;
    }

    public static int? OptionalColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().TrimStart('#');
        if (normalized.Length != 6
            || !int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int color))
        {
            throw new DiscordMessageValidationException(
                "Embed color must contain exactly six hexadecimal digits.");
        }

        return color;
    }

    public static void RequireScalarLimit(string? value, int maximum, string field)
    {
        if ((value ?? string.Empty).EnumerateRunes().Count() > maximum)
        {
            throw new DiscordMessageValidationException(
                $"{field} exceeds Discord's {maximum:N0}-character limit.");
        }
    }
}

public static class DiscordImageValidation
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumAltTextLength = 1_024;

    public static DiscordValidatedImage Validate(
        ReadOnlyMemory<byte> bytes,
        string? suppliedFileName,
        string? contentType,
        string? altText,
        bool spoiler,
        bool embedPlacement,
        Guid submissionId)
    {
        if (bytes.Length is < 4 or > MaximumBytes)
        {
            throw new DiscordMessageValidationException(
                "The image must be a supported file no larger than 8 MiB.");
        }

        string extension = DetectExtension(bytes.Span);
        string suppliedExtension = Path.GetExtension(suppliedFileName ?? string.Empty).ToLowerInvariant();
        string expectedType = extension switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => throw new UnreachableException(),
        };
        bool extensionMatches = suppliedExtension == extension
            || (extension == ".jpg" && suppliedExtension == ".jpeg");
        if (!extensionMatches
            || !string.Equals(contentType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiscordMessageValidationException(
                "The image extension, content type, and file signature must agree.");
        }

        DiscordMessageValidation.RequireScalarLimit(
            altText,
            MaximumAltTextLength,
            "Image alt text");
        return new DiscordValidatedImage(
            bytes.ToArray(),
            $"image-{submissionId:N}{extension}",
            expectedType,
            altText?.Trim(),
            spoiler,
            embedPlacement);
    }

    private static string DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return ".jpg";
        }

        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return ".gif";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        throw new DiscordMessageValidationException("The image format is not supported.");
    }
}

public sealed class DiscordMessageValidationException(string message) : Exception(message);
