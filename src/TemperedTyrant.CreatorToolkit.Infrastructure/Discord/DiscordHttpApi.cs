using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

internal sealed class DiscordHttpApi(HttpClient httpClient) : IDiscordApi
{
    internal static readonly Uri ApiBaseAddress = new("https://discord.com/api/v10/");
    internal const int MaximumErrorBytes = 16 * 1024;
    private const int MaximumSuccessBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public async Task<DiscordBotIdentity> ValidateBotAsync(
        string token,
        CancellationToken cancellationToken)
    {
        DiscordUserDto user = await GetAsync<DiscordUserDto>(token, "users/@me", cancellationToken);
        if (!user.Bot || !DiscordSnowflake.IsValid(user.Id))
        {
            throw new DiscordApiAuthenticationException();
        }

        DiscordApplicationDto application = await GetAsync<DiscordApplicationDto>(
            token,
            "oauth2/applications/@me",
            cancellationToken);
        if (!DiscordSnowflake.IsValid(application.Id)
            || application.Bot is null
            || !string.Equals(application.Bot.Id, user.Id, StringComparison.Ordinal))
        {
            throw new DiscordApiAuthenticationException();
        }

        return new DiscordBotIdentity(user.Id, SafeName(user), application.Id);
    }

    public async Task<IReadOnlyList<DiscordGuild>> ListGuildsAsync(
        string token,
        CancellationToken cancellationToken)
    {
        DiscordGuildDto[] guilds = await GetAsync<DiscordGuildDto[]>(
            token,
            "users/@me/guilds?limit=200",
            cancellationToken);
        return guilds
            .Where(value => DiscordSnowflake.IsValid(value.Id))
            .Select(value => new DiscordGuild(value.Id, SafeSnapshot(value.Name, "Discord server"), value.Icon))
            .ToArray();
    }

    public async Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
        string token,
        DiscordBotIdentity identity,
        string guildId,
        CancellationToken cancellationToken)
    {
        DiscordSnowflake.Require(guildId);
        DiscordGuild? guild = (await ListGuildsAsync(token, cancellationToken))
            .SingleOrDefault(value => value.Id == guildId);
        if (guild is null)
        {
            return null;
        }

        DiscordRoleDto[] roleDtos = await GetAsync<DiscordRoleDto[]>(
            token,
            $"guilds/{guildId}/roles",
            cancellationToken);
        DiscordGuildMemberDto memberDto;
        DiscordChannelDto[] channelDtos;
        try
        {
            memberDto = await GetAsync<DiscordGuildMemberDto>(
                token,
                $"guilds/{guildId}/members/{identity.BotUserId}",
                cancellationToken);
            channelDtos = await GetAsync<DiscordChannelDto[]>(
                token,
                $"guilds/{guildId}/channels",
                cancellationToken);
        }
        catch (DiscordApiNotFoundException)
        {
            return null;
        }

        DiscordRole[] roles = roleDtos
            .Where(value => DiscordSnowflake.IsValid(value.Id))
            .Select(value => new DiscordRole(
                value.Id,
                SafeSnapshot(value.Name, "Discord role"),
                value.Permissions,
                value.Mentionable))
            .ToArray();
        DiscordGuildMember member = MapMember(memberDto);
        DiscordChannelCapability[] channels = channelDtos
            .Where(value => value.Type is 0 or 5 && DiscordSnowflake.IsValid(value.Id))
            .Select(
                value => DiscordPermissionCalculator.Calculate(
                    guild,
                    member,
                    new DiscordChannel(
                        value.Id,
                        guild.Id,
                        SafeSnapshot(value.Name, "Discord channel"),
                        value.Type,
                        value.PermissionOverwrites
                            .Where(overwrite => DiscordSnowflake.IsValid(overwrite.Id))
                            .Select(overwrite => new DiscordPermissionOverwrite(
                                overwrite.Id,
                                overwrite.Type,
                                overwrite.Allow,
                                overwrite.Deny))
                            .ToArray()),
                    roles))
            .Where(value => value.CanView && value.CanSend)
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DiscordGuildDiscovery(guild, channels, roles, member);
    }

    public async Task<IReadOnlyList<DiscordGuildMember>> SearchMembersAsync(
        string token,
        string guildId,
        string query,
        CancellationToken cancellationToken)
    {
        DiscordSnowflake.Require(guildId);
        DiscordGuildMemberDto[] members = await GetAsync<DiscordGuildMemberDto[]>(
            token,
            $"guilds/{guildId}/members/search?query={Uri.EscapeDataString(query)}&limit=25",
            cancellationToken);
        return members
            .Where(value => value.User is not null && DiscordSnowflake.IsValid(value.User.Id))
            .Select(MapMember)
            .Take(25)
            .ToArray();
    }

    public async Task<DiscordGuildMember?> GetMemberAsync(
        string token,
        string guildId,
        string userId,
        CancellationToken cancellationToken)
    {
        DiscordSnowflake.Require(guildId);
        DiscordSnowflake.Require(userId);
        try
        {
            DiscordGuildMemberDto member = await GetAsync<DiscordGuildMemberDto>(
                token,
                $"guilds/{guildId}/members/{userId}",
                cancellationToken);
            return MapMember(member);
        }
        catch (DiscordApiNotFoundException)
        {
            return null;
        }
    }

    public async Task<DiscordApiSendResult> SendMessageAsync(
        string token,
        string channelId,
        DiscordMessageRequest request,
        DiscordValidatedImage? image,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendMessageCoreAsync(
                token,
                channelId,
                request,
                image,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordApiSendResult(DiscordDeliveryStatus.TimedOut);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new DiscordApiSendResult(DiscordDeliveryStatus.DiscordUnavailable);
        }
    }

    private async Task<DiscordApiSendResult> SendMessageCoreAsync(
        string token,
        string channelId,
        DiscordMessageRequest request,
        DiscordValidatedImage? image,
        CancellationToken cancellationToken)
    {
        DiscordSnowflake.Require(channelId);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpRequestMessage message = new(
                HttpMethod.Post,
                $"channels/{channelId}/messages");
            SetAuthorization(message, token);
            if (image is null)
            {
                message.Content = JsonContent.Create(request, options: JsonOptions);
            }
            else
            {
                MultipartFormDataContent multipart = new();
                multipart.Add(
                    new StringContent(
                        JsonSerializer.Serialize(request, JsonOptions),
                        Encoding.UTF8,
                        "application/json"),
                    "payload_json");
                ByteArrayContent file = new(image.Bytes);
                file.Headers.ContentType = MediaTypeHeaderValue.Parse(image.ContentType);
                multipart.Add(file, "files[0]", image.OutboundFileName);
                message.Content = multipart;
            }

            using HttpResponseMessage response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                DiscordMessageResponseDto body = await ReadJsonAsync<DiscordMessageResponseDto>(
                    response,
                    MaximumSuccessBytes,
                    cancellationToken);
                return new DiscordApiSendResult(
                    DiscordDeliveryStatus.Success,
                    DiscordSnowflake.IsValid(body.Id) ? body.Id : null);
            }

            DiscordDeliveryStatus status = Classify(response.StatusCode);
            if (status != DiscordDeliveryStatus.RateLimited || attempt > 0)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                return new DiscordApiSendResult(status);
            }

            TimeSpan? retryAfter = ParseRetryAfter(response);
            await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
            if (retryAfter is null || retryAfter <= TimeSpan.Zero || retryAfter > TimeSpan.FromSeconds(2))
            {
                return new DiscordApiSendResult(status, RetryAfter: retryAfter);
            }

            await Task.Delay(retryAfter.Value, cancellationToken);
        }

        return new DiscordApiSendResult(DiscordDeliveryStatus.RateLimited);
    }

    private async Task<T> GetAsync<T>(
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, path);
            SetAuthorization(request, token);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                throw new DiscordApiAuthenticationException();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                throw new DiscordApiNotFoundException();
            }

            if (!response.IsSuccessStatusCode)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                throw new DiscordApiUnavailableException();
            }

            return await ReadJsonAsync<T>(response, MaximumSuccessBytes, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiscordApiUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new DiscordApiUnavailableException();
        }
    }

    private static void SetAuthorization(HttpRequestMessage request, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(response, maximumBytes, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new DiscordApiUnavailableException();
        }
        catch (JsonException)
        {
            throw new DiscordApiUnavailableException();
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new(Math.Min(maximumBytes, 64 * 1024));
        byte[] chunk = new byte[8192];
        while (buffer.Length <= maximumBytes)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new DiscordApiUnavailableException();
            }

            buffer.Write(chunk, 0, read);
        }

        throw new DiscordApiUnavailableException();
    }

    private static async Task DrainBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await ReadBoundedAsync(response, maximumBytes, cancellationToken);
        }
        catch (DiscordApiUnavailableException)
        {
            // Oversized or malformed Discord error bodies are deliberately discarded.
        }
    }

    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            return null;
        }

        string? value = values.FirstOrDefault();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && double.IsFinite(seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    private static DiscordDeliveryStatus Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => DiscordDeliveryStatus.AuthenticationFailed,
        HttpStatusCode.Forbidden => DiscordDeliveryStatus.MissingPermission,
        HttpStatusCode.NotFound => DiscordDeliveryStatus.DestinationUnavailable,
        HttpStatusCode.BadRequest => DiscordDeliveryStatus.ValidationRejected,
        HttpStatusCode.TooManyRequests => DiscordDeliveryStatus.RateLimited,
        _ when (int)statusCode >= 500 => DiscordDeliveryStatus.DiscordUnavailable,
        _ => DiscordDeliveryStatus.UnexpectedFailure,
    };

    private static DiscordGuildMember MapMember(DiscordGuildMemberDto value)
    {
        DiscordUserDto user = value.User ?? throw new DiscordApiUnavailableException();
        return new DiscordGuildMember(
            DiscordSnowflake.Require(user.Id),
            SafeSnapshot(value.Nick ?? user.GlobalName ?? user.Username, "Discord member"),
            value.Roles.Where(DiscordSnowflake.IsValid).ToArray());
    }

    private static string SafeName(DiscordUserDto user) =>
        SafeSnapshot(user.GlobalName ?? user.Username, "Discord bot");

    private static string SafeSnapshot(string? value, string fallback)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is >= 1 and <= 100 ? normalized : fallback;
    }

    private sealed record DiscordUserDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("bot")] bool Bot);

    private sealed record DiscordApplicationDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("bot")] DiscordUserDto? Bot);

    private sealed record DiscordGuildDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string? Icon);

    private sealed record DiscordRoleDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("permissions")] string Permissions,
        [property: JsonPropertyName("mentionable")] bool Mentionable);

    private sealed record DiscordGuildMemberDto(
        [property: JsonPropertyName("user")] DiscordUserDto? User,
        [property: JsonPropertyName("nick")] string? Nick,
        [property: JsonPropertyName("roles")] string[] Roles);

    private sealed record DiscordChannelDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("guild_id")] string GuildId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("permission_overwrites")]
        DiscordOverwriteDto[] PermissionOverwrites);

    private sealed record DiscordOverwriteDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("allow")] string Allow,
        [property: JsonPropertyName("deny")] string Deny);

    private sealed record DiscordMessageResponseDto(
        [property: JsonPropertyName("id")] string Id);

    private sealed class DiscordApiNotFoundException : Exception;
}
