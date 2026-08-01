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

        return new DiscordBotIdentity(user.Id!, SafeName(user), application.Id!);
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
            .Select(value => new DiscordGuild(value.Id!, SafeSnapshot(value.Name, "Discord server"), value.Icon))
            .ToArray();
    }

    public async Task<DiscordGuildDiscovery?> DiscoverGuildAsync(
        string token,
        DiscordBotIdentity identity,
        string guildId,
        CancellationToken cancellationToken)
    {
        DiscordSnowflake.Require(guildId);
        DiscordGuildDto guildDto = await GetDiscoveryAsync<DiscordGuildDto>(
            token,
            $"guilds/{guildId}",
            DiscordDiscoveryStage.GuildResponse,
            cancellationToken);
        if (!DiscordSnowflake.IsValid(guildDto.Id)
            || !string.Equals(guildDto.Id, guildId, StringComparison.Ordinal))
        {
            throw new DiscordServerInformationException(
                DiscordDiscoveryStage.SnowflakeParsing,
                DiscordServerInformationFailure.UnsupportedResponse);
        }

        DiscordGuild guild = new(
            guildDto.Id!,
            SafeSnapshot(guildDto.Name, "Discord server"),
            guildDto.Icon);

        JsonElement[] channelDtos = await GetDiscoveryAsync<JsonElement[]>(
            token,
            $"guilds/{guildId}/channels",
            DiscordDiscoveryStage.ChannelListDeserialization,
            cancellationToken);
        DiscordRoleDto[] roleDtos = await GetDiscoveryAsync<DiscordRoleDto[]>(
            token,
            $"guilds/{guildId}/roles",
            DiscordDiscoveryStage.RoleListDeserialization,
            cancellationToken);
        DiscordGuildMemberDto memberDto = await GetDiscoveryAsync<DiscordGuildMemberDto>(
            token,
            $"guilds/{guildId}/members/{identity.BotUserId}",
            DiscordDiscoveryStage.BotMemberDeserialization,
            cancellationToken);

        try
        {
            DiscordRole[] roles = MapRoles(roleDtos);
            DiscordGuildMember member = MapBotMember(memberDto, identity.BotUserId);
            ValidateRoleAssignments(guild, member, roles);
            DiscordChannel[] supportedChannels = MapSupportedChannels(channelDtos, guild.Id);
            DiscordChannelCapability[] channels = supportedChannels
                .Select(value => CalculatePermissions(guild, member, value, roles))
                .Where(value => value.CanView && value.CanSend)
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new DiscordGuildDiscovery(guild, channels, roles, member);
        }
        catch (DiscordServerInformationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new DiscordServerInformationException(
                DiscordDiscoveryStage.ViewModelGeneration,
                DiscordServerInformationFailure.ProcessingFailed);
        }
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
        TimeSpan? retryAfter = status == DiscordDeliveryStatus.RateLimited
            ? ParseRetryAfter(response)
            : null;
        await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
        return new DiscordApiSendResult(status, RetryAfter: retryAfter);
    }

    private async Task<T> GetAsync<T>(
        string token,
        string path,
        CancellationToken cancellationToken,
        bool preserveDiscoveryFailure = false)
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

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                throw new DiscordApiForbiddenException();
            }

            if (!response.IsSuccessStatusCode)
            {
                await DrainBoundedAsync(response, MaximumErrorBytes, cancellationToken);
                if ((int)response.StatusCode is >= 400 and < 500
                    && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    throw new DiscordApiUnsupportedResponseException();
                }

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
        catch (DiscordApiUnsupportedResponseException) when (!preserveDiscoveryFailure)
        {
            throw new DiscordApiUnavailableException();
        }
        catch (DiscordApiForbiddenException) when (!preserveDiscoveryFailure)
        {
            throw new DiscordApiUnavailableException();
        }
    }

    private async Task<T> GetDiscoveryAsync<T>(
        string token,
        string path,
        DiscordDiscoveryStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync<T>(token, path, cancellationToken, preserveDiscoveryFailure: true);
        }
        catch (DiscordApiAuthenticationException)
        {
            throw new DiscordServerInformationException(
                stage,
                DiscordServerInformationFailure.AuthenticationFailed);
        }
        catch (DiscordApiNotFoundException)
        {
            throw new DiscordServerInformationException(
                stage,
                DiscordServerInformationFailure.NotInstalled);
        }
        catch (DiscordApiForbiddenException)
        {
            throw new DiscordServerInformationException(
                stage,
                DiscordServerInformationFailure.AccessDenied);
        }
        catch (DiscordApiUnsupportedResponseException)
        {
            throw new DiscordServerInformationException(
                stage,
                DiscordServerInformationFailure.UnsupportedResponse);
        }
        catch (DiscordApiUnavailableException)
        {
            throw new DiscordServerInformationException(
                stage,
                DiscordServerInformationFailure.TemporarilyUnavailable);
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
                ?? throw new DiscordApiUnsupportedResponseException();
        }
        catch (JsonException)
        {
            throw new DiscordApiUnsupportedResponseException();
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
            (value.Roles ?? []).Where(DiscordSnowflake.IsValid).ToArray());
    }

    private static DiscordRole[] MapRoles(IReadOnlyList<DiscordRoleDto> values)
    {
        var roles = new List<DiscordRole>(values.Count);
        foreach (DiscordRoleDto value in values)
        {
            if (!DiscordSnowflake.IsValid(value.Id))
            {
                throw Failure(DiscordDiscoveryStage.SnowflakeParsing);
            }

            string? permissions = OptionalString(value.Permissions);
            if (permissions is null)
            {
                throw Failure(DiscordDiscoveryStage.PermissionBitParsing);
            }

            _ = ParsePermission(permissions);
            roles.Add(new DiscordRole(
                value.Id!,
                SafeSnapshot(value.Name, "Discord role"),
                permissions,
                value.Mentionable));
        }

        return roles.ToArray();
    }

    private static DiscordGuildMember MapBotMember(
        DiscordGuildMemberDto value,
        string expectedBotId)
    {
        string userId = value.User?.Id ?? expectedBotId;
        if (!DiscordSnowflake.IsValid(userId)
            || !string.Equals(userId, expectedBotId, StringComparison.Ordinal))
        {
            throw Failure(DiscordDiscoveryStage.SnowflakeParsing);
        }

        string[] roleIds = value.Roles ?? [];
        if (roleIds.Any(roleId => !DiscordSnowflake.IsValid(roleId)))
        {
            throw Failure(DiscordDiscoveryStage.SnowflakeParsing);
        }

        return new DiscordGuildMember(
            userId,
            SafeSnapshot(value.Nick ?? value.User?.GlobalName ?? value.User?.Username, "Discord bot"),
            roleIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateRoleAssignments(
        DiscordGuild guild,
        DiscordGuildMember member,
        IReadOnlyList<DiscordRole> roles)
    {
        HashSet<string> roleIds = roles.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (!roleIds.Contains(guild.Id) || member.RoleIds.Any(roleId => !roleIds.Contains(roleId)))
        {
            throw Failure(DiscordDiscoveryStage.RoleAssignment);
        }
    }

    private static DiscordChannel[] MapSupportedChannels(
        IReadOnlyList<JsonElement> values,
        string guildId)
    {
        var channels = new List<DiscordChannel>();
        foreach (JsonElement value in values)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("type", out JsonElement typeElement)
                || !TryGetChannelType(typeElement, out int type)
                || type is not (0 or 5))
            {
                continue;
            }

            value.TryGetProperty("id", out JsonElement idElement);
            value.TryGetProperty("guild_id", out JsonElement guildElement);
            value.TryGetProperty("name", out JsonElement nameElement);
            value.TryGetProperty("permission_overwrites", out JsonElement overwriteElement);
            string? channelId = OptionalString(idElement);
            string? responseGuildId = OptionalString(guildElement);
            if (!DiscordSnowflake.IsValid(channelId)
                || (guildElement.ValueKind is not (
                        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                || (responseGuildId is not null
                    && !string.Equals(responseGuildId, guildId, StringComparison.Ordinal)))
            {
                throw Failure(DiscordDiscoveryStage.SnowflakeParsing);
            }

            channels.Add(new DiscordChannel(
                channelId!,
                guildId,
                SafeSnapshot(OptionalString(nameElement), "Discord channel"),
                type,
                MapOverwrites(overwriteElement)));
        }

        return channels.ToArray();
    }

    private static DiscordPermissionOverwrite[] MapOverwrites(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Failure(DiscordDiscoveryStage.ChannelOverwriteParsing);
        }

        var overwrites = new List<DiscordPermissionOverwrite>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !DiscordSnowflake.IsValid(idElement.GetString())
                || !item.TryGetProperty("type", out JsonElement typeElement)
                || !typeElement.TryGetInt32(out int type)
                || type is not (0 or 1))
            {
                throw Failure(DiscordDiscoveryStage.ChannelOverwriteParsing);
            }

            string allow = OptionalPermission(item, "allow");
            string deny = OptionalPermission(item, "deny");
            overwrites.Add(new DiscordPermissionOverwrite(
                idElement.GetString()!,
                type,
                allow,
                deny));
        }

        return overwrites.ToArray();
    }

    private static string OptionalPermission(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return "0";
        }

        string? permission = OptionalString(element);
        if (permission is null)
        {
            throw Failure(DiscordDiscoveryStage.ChannelOverwriteParsing);
        }

        try
        {
            _ = DiscordPermissionCalculator.ParsePermission(permission);
        }
        catch (DiscordPermissionDataException)
        {
            throw Failure(DiscordDiscoveryStage.ChannelOverwriteParsing);
        }

        return permission;
    }

    private static DiscordChannelCapability CalculatePermissions(
        DiscordGuild guild,
        DiscordGuildMember member,
        DiscordChannel channel,
        IReadOnlyList<DiscordRole> roles)
    {
        try
        {
            return DiscordPermissionCalculator.Calculate(guild, member, channel, roles);
        }
        catch (DiscordPermissionDataException)
        {
            throw Failure(DiscordDiscoveryStage.EffectivePermissionCalculation);
        }
    }

    private static System.Numerics.BigInteger ParsePermission(string value)
    {
        try
        {
            return DiscordPermissionCalculator.ParsePermission(value);
        }
        catch (DiscordPermissionDataException)
        {
            throw Failure(DiscordDiscoveryStage.PermissionBitParsing);
        }
    }

    private static bool TryGetChannelType(JsonElement value, out int type)
    {
        type = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out type);
    }

    private static string? OptionalString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DiscordServerInformationException Failure(DiscordDiscoveryStage stage) =>
        new(stage, DiscordServerInformationFailure.UnsupportedResponse);

    private static string SafeName(DiscordUserDto user) =>
        SafeSnapshot(user.GlobalName ?? user.Username, "Discord bot");

    private static string SafeSnapshot(string? value, string fallback)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is >= 1 and <= 100 ? normalized : fallback;
    }

    private sealed record DiscordUserDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("bot")] bool Bot);

    private sealed record DiscordApplicationDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("bot")] DiscordUserDto? Bot);

    private sealed record DiscordGuildDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("icon")] string? Icon);

    private sealed record DiscordRoleDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("permissions")] JsonElement Permissions,
        [property: JsonPropertyName("mentionable")] bool Mentionable);

    private sealed record DiscordGuildMemberDto(
        [property: JsonPropertyName("user")] DiscordUserDto? User,
        [property: JsonPropertyName("nick")] string? Nick,
        [property: JsonPropertyName("roles")] string[]? Roles);

    private sealed record DiscordMessageResponseDto(
        [property: JsonPropertyName("id")] string Id);

    private sealed class DiscordApiNotFoundException : Exception;

    private sealed class DiscordApiForbiddenException : Exception;

    private sealed class DiscordApiUnsupportedResponseException : Exception;
}
