using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class DiscordProtocolTests
{
    private const string GuildId = "100000000000000001";
    private const string BotId = "100000000000000002";
    private const string RoleId = "100000000000000003";
    private const string UserId = "100000000000000004";
    private const string ChannelId = "100000000000000005";

    [Theory]
    [InlineData("1", true)]
    [InlineData("18446744073709551615", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("123x", false)]
    [InlineData("18446744073709551616", false)]
    public void SnowflakesAreValidatedLosslessly(string value, bool expected)
    {
        Assert.Equal(expected, DiscordSnowflake.IsValid(value));
    }

    [Fact]
    public void PermissionCalculatorHonorsRoleAndMemberOverwritesAndImplicitDenials()
    {
        DiscordGuild guild = new(GuildId, "Guild");
        DiscordGuildMember bot = new(BotId, "Bot", [RoleId]);
        DiscordRole[] roles =
        [
            new(GuildId, "everyone", PermissionText(DiscordPermissions.ViewChannel | DiscordPermissions.SendMessages), false),
            new(RoleId, "Bot role", PermissionText(DiscordPermissions.EmbedLinks | DiscordPermissions.AttachFiles), false),
        ];
        DiscordChannel channel = new(
            ChannelId,
            GuildId,
            "updates",
            0,
            [
                new(RoleId, 0, "0", PermissionText(DiscordPermissions.SendMessages)),
                new(BotId, 1, PermissionText(DiscordPermissions.SendMessages), "0"),
            ]);

        DiscordChannelCapability result = DiscordPermissionCalculator.Calculate(guild, bot, channel, roles);

        Assert.True(result.CanView);
        Assert.True(result.CanSend);
        Assert.True(result.CanEmbed);
        Assert.True(result.CanAttach);

        DiscordChannel hidden = channel with
        {
            Overwrites = [new(GuildId, 0, "0", PermissionText(DiscordPermissions.ViewChannel))],
        };
        DiscordChannelCapability denied = DiscordPermissionCalculator.Calculate(guild, bot, hidden, roles);
        Assert.False(denied.CanView);
        Assert.False(denied.CanSend);
        Assert.False(denied.CanEmbed);
        Assert.False(denied.CanAttach);
    }

    [Fact]
    public void AdministratorBypassesChannelOverwrites()
    {
        DiscordGuild guild = new(GuildId, "Guild");
        DiscordGuildMember bot = new(BotId, "Bot", []);
        DiscordRole everyone = new(GuildId, "everyone", PermissionText(DiscordPermissions.Administrator), false);
        DiscordChannel channel = new(
            ChannelId,
            GuildId,
            "updates",
            0,
            [new(BotId, 1, "0", ulong.MaxValue.ToString(CultureInfo.InvariantCulture))]);

        DiscordChannelCapability result = DiscordPermissionCalculator.Calculate(guild, bot, channel, [everyone]);

        Assert.True(result.CanView);
        Assert.True(result.CanSend);
        Assert.True(result.CanMentionEveryone);
    }

    [Fact]
    public void MentionPayloadsNeverEnableBroadRoleOrUserParsing()
    {
        DiscordMentionBuildResult none = DiscordMentionSelection.None.Build();
        Assert.Empty(none.AllowedMentions.Parse);
        Assert.Null(none.AllowedMentions.Roles);
        Assert.Null(none.AllowedMentions.Users);

        DiscordMentionBuildResult selected = new DiscordMentionSelection(
            Everyone: true,
            Here: false,
            [RoleId],
            [UserId]).Build();
        Assert.Equal(["everyone"], selected.AllowedMentions.Parse);
        Assert.Equal([RoleId], selected.AllowedMentions.Roles);
        Assert.Equal([UserId], selected.AllowedMentions.Users);
        Assert.Equal($"@everyone <@&{RoleId}> <@{UserId}>", selected.VisiblePrefix);

        string json = JsonSerializer.Serialize(selected.AllowedMentions);
        Assert.DoesNotContain("\"users\"", JsonSerializer.Serialize(selected.AllowedMentions.Parse), StringComparison.Ordinal);
        Assert.DoesNotContain("\"roles\"", JsonSerializer.Serialize(selected.AllowedMentions.Parse), StringComparison.Ordinal);
        Assert.Contains($"\"roles\":[\"{RoleId}\"]", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "", "[]")]
    [InlineData(true, false, "@everyone", "[\"everyone\"]")]
    [InlineData(false, true, "@here", "[\"everyone\"]")]
    [InlineData(true, true, "@everyone @here", "[\"everyone\"]")]
    public void MassMentionCombinationsSerializeOnlyTheExplicitEveryoneParse(
        bool everyone,
        bool here,
        string expectedPrefix,
        string expectedParse)
    {
        DiscordMentionBuildResult result = new DiscordMentionSelection(
            everyone,
            here,
            [],
            []).Build();

        Assert.Equal(expectedPrefix, result.VisiblePrefix);
        Assert.Equal(expectedParse, JsonSerializer.Serialize(result.AllowedMentions.Parse));
        Assert.Null(result.AllowedMentions.Roles);
        Assert.Null(result.AllowedMentions.Users);
    }

    [Fact]
    public void NonceIsStableDistinctAndAtMostTwentyFiveCharacters()
    {
        Guid submission = new("b8f54bfb-e580-4d6e-8eb4-cd86ed67be3c");
        string first = DiscordNonce.Create(submission, ChannelId);

        Assert.Equal(first, DiscordNonce.Create(submission, ChannelId));
        Assert.NotEqual(first, DiscordNonce.Create(submission, "100000000000000006"));
        Assert.InRange(first.Length, 1, 25);
        Assert.Matches("^[0-9a-f]+$", first);
    }

    [Fact]
    public void UrlColorAndImageValidationFailClosed()
    {
        Assert.Equal("https", DiscordMessageValidation.OptionalHttpsUri("https://example.invalid/image.png", "Image")!.Scheme);
        Assert.Throws<DiscordMessageValidationException>(() => DiscordMessageValidation.OptionalHttpsUri("http://127.0.0.1/x", "Image"));
        Assert.Throws<DiscordMessageValidationException>(() => DiscordMessageValidation.OptionalHttpsUri("https://user:pass@example.invalid/x", "Image"));
        Assert.Equal(0x74c7a5, DiscordMessageValidation.OptionalColor("#74c7a5"));

        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
        DiscordValidatedImage image = DiscordImageValidation.Validate(
            png,
            "untrusted.png",
            "image/png",
            "Safe alt",
            spoiler: true,
            embedPlacement: true,
            Guid.Empty);
        Assert.Equal("image-00000000000000000000000000000000.png", image.OutboundFileName);
        Assert.True(image.Spoiler);
        Assert.True(image.EmbedPlacement);
        Assert.Throws<DiscordMessageValidationException>(() => DiscordImageValidation.Validate(
            png,
            "wrong.jpg",
            "image/jpeg",
            null,
            false,
            false,
            Guid.Empty));
    }

    [Fact]
    public async Task HttpTransportUsesFixedHostBotAuthorizationAndExactNoMentionPayload()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = DiscordHttpApi.ApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var api = new DiscordHttpApi(client);
        DiscordMessageRequest request = new(
            "Safe content",
            null,
            DiscordMentionSelection.None.Build().AllowedMentions,
            4,
            DiscordNonce.Create(Guid.Empty, ChannelId),
            true,
            null);

        DiscordApiSendResult result = await api.SendMessageAsync(
            "synthetic-test-bot-token",
            ChannelId,
            request,
            null,
            CancellationToken.None);

        Assert.Equal(DiscordDeliveryStatus.Success, result.Status);
        Assert.Equal(new Uri($"https://discord.com/api/v10/channels/{ChannelId}/messages"), handler.Uri);
        Assert.Equal("Bot", handler.AuthorizationScheme);
        Assert.True(
            string.Equals(
                "synthetic-test-bot-token",
                handler.AuthorizationParameter,
                StringComparison.Ordinal),
            "The controlled transport did not receive the expected synthetic credential.");
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.True(body.RootElement.GetProperty("enforce_nonce").GetBoolean());
        Assert.Equal(4, body.RootElement.GetProperty("flags").GetInt32());
    }

    [Fact]
    public async Task IdentityGuildChannelRoleAndMemberDiscoveryUseOfficialV10Routes()
    {
        var handler = new ContractHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = DiscordHttpApi.ApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var api = new DiscordHttpApi(client);
        const string token = "synthetic-discovery-token";

        DiscordBotIdentity identity = await api.ValidateBotAsync(token, CancellationToken.None);
        IReadOnlyList<DiscordGuild> guilds = await api.ListGuildsAsync(token, CancellationToken.None);
        DiscordGuildDiscovery? discovery = await api.DiscoverGuildAsync(
            token,
            identity,
            GuildId,
            CancellationToken.None);
        IReadOnlyList<DiscordGuildMember> members = await api.SearchMembersAsync(
            token,
            GuildId,
            "safe search",
            CancellationToken.None);
        DiscordGuildMember? member = await api.GetMemberAsync(
            token,
            GuildId,
            UserId,
            CancellationToken.None);

        Assert.Equal(BotId, identity.BotUserId);
        Assert.Single(guilds);
        Assert.NotNull(discovery);
        Assert.Single(discovery.Channels);
        Assert.Single(members);
        Assert.Equal(UserId, member?.UserId);
        Assert.Contains("/api/v10/users/@me", handler.Paths);
        Assert.Contains("/api/v10/oauth2/applications/@me", handler.Paths);
        Assert.Contains($"/api/v10/guilds/{GuildId}/roles", handler.Paths);
        Assert.Contains($"/api/v10/guilds/{GuildId}/channels", handler.Paths);
        Assert.Contains($"/api/v10/guilds/{GuildId}/members/{BotId}", handler.Paths);
        Assert.Contains($"/api/v10/guilds/{GuildId}/members/{UserId}", handler.Paths);
        Assert.Contains(
            handler.RequestUris,
            value => value.PathAndQuery.EndsWith(
                $"/guilds/{GuildId}/members/search?query=safe%20search&limit=25",
                StringComparison.Ordinal));
        Assert.All(handler.Hosts, value => Assert.Equal("discord.com", value));
        Assert.All(handler.AuthorizationSchemes, value => Assert.Equal("Bot", value));
    }

    [Fact]
    public async Task MultipartMessageUsesOfficialFieldNamesAndSpoilerAttachmentMetadata()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = DiscordHttpApi.ApiBaseAddress };
        var api = new DiscordHttpApi(client);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
        DiscordValidatedImage image = DiscordImageValidation.Validate(
            png,
            "safe.png",
            "image/png",
            "Alt",
            true,
            true,
            Guid.Empty);
        DiscordMessageRequest request = new(
            null,
            [new DiscordEmbedPayload(null, "Description", null, null, null, new DiscordEmbedMedia($"attachment://{image.OutboundFileName}"), null)],
            DiscordMentionSelection.None.Build().AllowedMentions,
            0,
            DiscordNonce.Create(Guid.Empty, ChannelId),
            true,
            [new DiscordAttachmentPayload(0, image.OutboundFileName, image.AltText, true)]);

        _ = await api.SendMessageAsync(
            "synthetic-multipart-token",
            ChannelId,
            request,
            image,
            CancellationToken.None);

        Assert.StartsWith("multipart/form-data", handler.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=payload_json", handler.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"files[0]\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"is_spoiler\":true", handler.Body, StringComparison.Ordinal);
        Assert.Contains($"attachment://{image.OutboundFileName}", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOILER_", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterParsingIsBoundedAndCultureInvariant()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "0.125");
        Assert.Equal(TimeSpan.FromMilliseconds(125), DiscordHttpApi.ParseRetryAfter(response));
    }

    [Fact]
    public async Task RateLimitRetriesAtMostOnceOnlyWhenHeaderFitsForegroundBound()
    {
        var shortHandler = new SequenceHandler(
            CreateRateLimited("0.001"),
            CreateSuccess());
        using var shortClient = new HttpClient(shortHandler) { BaseAddress = DiscordHttpApi.ApiBaseAddress };
        DiscordApiSendResult recovered = await new DiscordHttpApi(shortClient).SendMessageAsync(
            "synthetic-rate-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);
        Assert.Equal(DiscordDeliveryStatus.Success, recovered.Status);
        Assert.Equal(2, shortHandler.CallCount);

        var longHandler = new SequenceHandler(CreateRateLimited("5"), CreateSuccess());
        using var longClient = new HttpClient(longHandler) { BaseAddress = DiscordHttpApi.ApiBaseAddress };
        DiscordApiSendResult limited = await new DiscordHttpApi(longClient).SendMessageAsync(
            "synthetic-rate-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);
        Assert.Equal(DiscordDeliveryStatus.RateLimited, limited.Status);
        Assert.Equal(1, longHandler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DiscordDeliveryStatus.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, DiscordDeliveryStatus.MissingPermission)]
    [InlineData(HttpStatusCode.NotFound, DiscordDeliveryStatus.DestinationUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, DiscordDeliveryStatus.ValidationRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, DiscordDeliveryStatus.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DiscordDeliveryStatus.DiscordUnavailable)]
    public async Task DiscordFailuresMapToFixedSafeCategories(
        HttpStatusCode statusCode,
        DiscordDeliveryStatus expected)
    {
        var handler = new SequenceHandler(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("synthetic body that is never returned"),
        });
        using var client = new HttpClient(handler) { BaseAddress = DiscordHttpApi.ApiBaseAddress };

        DiscordApiSendResult result = await new DiscordHttpApi(client).SendMessageAsync(
            "synthetic-classification-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.MessageId);
    }

    [Fact]
    public async Task OversizedDiscordErrorBodyIsDiscardedAndCancellationIsObserved()
    {
        var oversized = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ByteArrayContent(new byte[DiscordHttpApi.MaximumErrorBytes + 1]),
        });
        using var errorClient = new HttpClient(oversized) { BaseAddress = DiscordHttpApi.ApiBaseAddress };
        DiscordApiSendResult result = await new DiscordHttpApi(errorClient).SendMessageAsync(
            "synthetic-error-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);
        Assert.Equal(DiscordDeliveryStatus.DiscordUnavailable, result.Status);

        var blocking = new BlockingHandler();
        using var blockingClient = new HttpClient(blocking) { BaseAddress = DiscordHttpApi.ApiBaseAddress };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiscordHttpApi(blockingClient).SendMessageAsync(
                "synthetic-cancel-token",
                ChannelId,
                CreateSafeMessage(),
                null,
                cancellation.Token));
    }

    [Fact]
    public async Task TransportFailuresAndInternalTimeoutsAreClassifiedWithoutHidingCallerCancellation()
    {
        using var failedClient = new HttpClient(new ThrowingHandler(new HttpRequestException()))
        {
            BaseAddress = DiscordHttpApi.ApiBaseAddress,
        };
        DiscordApiSendResult unavailable = await new DiscordHttpApi(failedClient).SendMessageAsync(
            "synthetic-failure-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);
        Assert.Equal(DiscordDeliveryStatus.DiscordUnavailable, unavailable.Status);

        using var timedOutClient = new HttpClient(new ThrowingHandler(new TaskCanceledException()))
        {
            BaseAddress = DiscordHttpApi.ApiBaseAddress,
        };
        DiscordApiSendResult timedOut = await new DiscordHttpApi(timedOutClient).SendMessageAsync(
            "synthetic-timeout-token",
            ChannelId,
            CreateSafeMessage(),
            null,
            CancellationToken.None);
        Assert.Equal(DiscordDeliveryStatus.TimedOut, timedOut.Status);

        await Assert.ThrowsAsync<DiscordApiUnavailableException>(() =>
            new DiscordHttpApi(failedClient).ListGuildsAsync(
                "synthetic-failure-token",
                CancellationToken.None));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? Uri { get; private set; }

        internal string? AuthorizationScheme { get; private set; }

        internal string? AuthorizationParameter { get; private set; }

        internal string? Body { get; private set; }

        internal string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"100000000000000099\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static DiscordMessageRequest CreateSafeMessage() => new(
        "Safe",
        null,
        DiscordMentionSelection.None.Build().AllowedMentions,
        0,
        DiscordNonce.Create(Guid.Empty, ChannelId),
        true,
        null);

    private static HttpResponseMessage CreateRateLimited(string retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static HttpResponseMessage CreateSuccess() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"id\":\"100000000000000099\"}", Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        internal int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled blocking handler completed unexpectedly.");
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        internal List<Uri> RequestUris { get; } = [];

        internal List<string> Hosts { get; } = [];

        internal List<string?> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Paths.Add(uri.AbsolutePath);
            RequestUris.Add(uri);
            Hosts.Add(uri.Host);
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            string json = uri.AbsolutePath switch
            {
                "/api/v10/users/@me" => $"{{\"id\":\"{BotId}\",\"username\":\"bot\",\"global_name\":\"Creator Toolkit\",\"bot\":true}}",
                "/api/v10/oauth2/applications/@me" => $"{{\"id\":\"100000000000000099\",\"bot\":{{\"id\":\"{BotId}\",\"username\":\"bot\",\"global_name\":null,\"bot\":true}}}}",
                "/api/v10/users/@me/guilds" => $"[{{\"id\":\"{GuildId}\",\"name\":\"Creators\",\"icon\":null}}]",
                var path when path.EndsWith("/roles", StringComparison.Ordinal) => $"[{{\"id\":\"{GuildId}\",\"name\":\"everyone\",\"permissions\":\"{DiscordPermissionCalculator.StandardInstallPermissions}\",\"mentionable\":false}}]",
                var path when path.EndsWith("/channels", StringComparison.Ordinal) => $"[{{\"id\":\"{ChannelId}\",\"guild_id\":\"{GuildId}\",\"name\":\"announcements\",\"type\":0,\"permission_overwrites\":[]}}]",
                var path when path.EndsWith($"/members/{BotId}", StringComparison.Ordinal) => $"{{\"user\":{{\"id\":\"{BotId}\",\"username\":\"bot\",\"global_name\":null,\"bot\":true}},\"nick\":null,\"roles\":[]}}",
                var path when path.EndsWith($"/members/{UserId}", StringComparison.Ordinal) => $"{{\"user\":{{\"id\":\"{UserId}\",\"username\":\"member\",\"global_name\":null,\"bot\":false}},\"nick\":null,\"roles\":[]}}",
                var path when path.EndsWith("/members/search", StringComparison.Ordinal) => $"[{{\"user\":{{\"id\":\"{UserId}\",\"username\":\"member\",\"global_name\":null,\"bot\":false}},\"nick\":null,\"roles\":[]}}]",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string PermissionText(DiscordPermissions permissions) =>
        ((ulong)permissions).ToString(CultureInfo.InvariantCulture);
}
