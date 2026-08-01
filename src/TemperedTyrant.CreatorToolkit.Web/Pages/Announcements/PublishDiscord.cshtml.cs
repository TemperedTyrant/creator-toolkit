using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Discord;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ContentEditing)]
[SensitiveSecurityHeaderProfile]
[RequestSizeLimit(9 * 1024 * 1024)]
public sealed class PublishDiscordModel(
    IDiscordPublishingService publishing,
    IDiscordConfigurationService configuration,
    DiscordPublicationResultStore resultStore,
    IDataProtectionProvider dataProtectionProvider) : PageModel
{
    private const string ReviewProtectionPurpose =
        "TemperedTyrant.CreatorToolkit.DiscordPublicationReview.v1";
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(10);
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid ConnectionId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string GuildId { get; set; } = string.Empty;

    [BindProperty]
    public Guid SubmissionId { get; set; }

    [BindProperty]
    public long AnnouncementRevision { get; set; }

    [BindProperty]
    public IReadOnlyList<Guid> DestinationIds { get; set; } = [];

    [BindProperty]
    public DiscordMessageMode Mode { get; set; } = DiscordMessageMode.Plain;

    [BindProperty]
    public string? PlainContent { get; set; }

    [BindProperty]
    public bool ShowLinkPreviews { get; set; } = true;

    [BindProperty]
    public string? EmbedMessageText { get; set; }

    [BindProperty]
    public string? EmbedTitle { get; set; }

    [BindProperty]
    public string? EmbedDescription { get; set; }

    [BindProperty]
    public string? EmbedTitleUrl { get; set; }

    [BindProperty]
    public string? EmbedColor { get; set; }

    [BindProperty]
    public string? EmbedFooter { get; set; }

    [BindProperty]
    public string? EmbedImageUrl { get; set; }

    [BindProperty]
    public string? EmbedThumbnailUrl { get; set; }

    [BindProperty]
    public string? RemoteImageUrl { get; set; }

    [BindProperty]
    public IFormFile? UploadedImage { get; set; }

    [BindProperty]
    public string? ImageAltText { get; set; }

    [BindProperty]
    public bool ImageSpoiler { get; set; }

    [BindProperty]
    public bool ImageInEmbed { get; set; }

    [BindProperty]
    public bool MentionEveryone { get; set; }

    [BindProperty]
    public bool MentionHere { get; set; }

    [BindProperty]
    public IReadOnlyList<string> RoleIds { get; set; } = [];

    [BindProperty]
    public IReadOnlyList<string> UserIds { get; set; } = [];

    [BindProperty]
    public string? ManualUserId { get; set; }

    [BindProperty]
    public string? MemberQuery { get; set; }

    [BindProperty]
    public bool MassMentionConfirmed { get; set; }

    [BindProperty]
    public bool FinalConfirmation { get; set; }

    [BindProperty]
    public bool ReviewComplete { get; set; }

    [BindProperty]
    public string? ReviewToken { get; set; }

    public DiscordPublishContext? Context { get; private set; }

    public DiscordGuildDiscovery? Discovery { get; private set; }

    public IReadOnlyList<DiscordGuildMember> MemberResults { get; private set; } = [];

    public bool CanUseMassMentions => User.IsInRole(SystemRoles.Owner) || User.IsInRole(SystemRoles.Admin);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        SubmissionId = Guid.NewGuid();
        AnnouncementRevision = Context!.AnnouncementRevision;
        PlainContent = $"**{Context.AnnouncementTitle}**\n\n{Context.AnnouncementBody}";
        EmbedTitle = Context.AnnouncementTitle;
        EmbedDescription = Context.AnnouncementBody;
        return Page();
    }

    public async Task<IActionResult> OnPostSearchMembersAsync(CancellationToken cancellationToken)
    {
        ReviewComplete = false;
        ReviewToken = null;
        FinalConfirmation = false;
        ModelState.Remove(nameof(ReviewComplete));
        ModelState.Remove(nameof(ReviewToken));
        ModelState.Remove(nameof(FinalConfirmation));
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        try
        {
            MemberResults = await publishing.SearchMembersAsync(
                ConnectionId,
                GuildId,
                MemberQuery ?? string.Empty,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or DiscordApiAuthenticationException or DiscordApiUnavailableException)
        {
            ModelState.AddModelError(nameof(MemberQuery), "Member search is unavailable. Use a Discord user ID instead.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken cancellationToken)
    {
        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        if (SubmissionId == Guid.Empty)
        {
            ModelState.AddModelError(string.Empty, "Reload the publication form before sending.");
        }

        if (DestinationIds.Distinct().Count() is < 1 or > 10)
        {
            ModelState.AddModelError(nameof(DestinationIds), "Select between 1 and 10 Discord channels.");
        }

        if ((MentionEveryone || MentionHere) && (!CanUseMassMentions || !MassMentionConfirmed))
        {
            ModelState.AddModelError(nameof(MassMentionConfirmed), "Mass mentions require the high-impact confirmation.");
        }

        DiscordValidatedImage? image = null;
        try
        {
            image = await ReadImageAsync(cancellationToken);
        }
        catch (DiscordPublicationValidationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }

        ReviewComplete = ModelState.IsValid;
        ReviewToken = ReviewComplete
            ? CreateReviewToken(actor.Value, image)
            : null;
        FinalConfirmation = false;
        ModelState.Remove(nameof(ReviewComplete));
        ModelState.Remove(nameof(ReviewToken));
        ModelState.Remove(nameof(FinalConfirmation));
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken cancellationToken)
    {
        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        if (!ReviewComplete || !FinalConfirmation)
        {
            ModelState.AddModelError(nameof(FinalConfirmation), "Review and confirm the Discord publication before sending.");
        }

        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        try
        {
            List<string> users = UserIds.ToList();
            if (!string.IsNullOrWhiteSpace(ManualUserId))
            {
                DiscordGuildMember? member = await publishing.ValidateMemberAsync(
                    ConnectionId,
                    GuildId,
                    ManualUserId.Trim(),
                    cancellationToken);
                if (member is null)
                {
                    ModelState.AddModelError(nameof(ManualUserId), "That Discord user is not a member of the selected server.");
                }
                else
                {
                    users.Add(member.UserId);
                }
            }

            DiscordValidatedImage? image = await ReadImageAsync(cancellationToken);
            if (!ReviewMatches(actor.Value, image))
            {
                ReviewComplete = false;
                ReviewToken = null;
                ModelState.Remove(nameof(ReviewComplete));
                ModelState.Remove(nameof(ReviewToken));
                ModelState.AddModelError(
                    string.Empty,
                    "The reviewed publication changed or expired. Review it again before sending.");
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            DiscordPublishRequest request = new(
                SubmissionId,
                Id,
                AnnouncementRevision,
                ConnectionId,
                GuildId,
                DestinationIds,
                Mode,
                PlainContent,
                ShowLinkPreviews,
                new DiscordEmbedInput(
                    EmbedMessageText,
                    EmbedTitle,
                    EmbedDescription,
                    EmbedTitleUrl,
                    EmbedColor,
                    EmbedFooter,
                    EmbedImageUrl,
                    EmbedThumbnailUrl),
                new DiscordMentionSelection(MentionEveryone, MentionHere, RoleIds, users),
                MassMentionConfirmed,
                RemoteImageUrl,
                image);
            DiscordPublicationResult result = await publishing.PublishAsync(
                request,
                CanUseMassMentions,
                actor.Value,
                cancellationToken);
            resultStore.Put(actor.Value, result);
            return RedirectToPage(
                "/Announcements/DiscordResult",
                new { id = Id, submissionId = SubmissionId });
        }
        catch (DiscordPublicationValidationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (Exception exception) when (exception is DiscordApiAuthenticationException or DiscordApiUnavailableException)
        {
            ModelState.AddModelError(string.Empty, "Discord is unavailable. No message content or credentials were exposed.");
            return Page();
        }
    }

    private async Task<bool> LoadAsync(CancellationToken token)
    {
        Context = await publishing.GetContextAsync(Id, token);
        if (Context is null)
        {
            return false;
        }

        if (ConnectionId != Guid.Empty && DiscordSnowflake.IsValid(GuildId))
        {
            try
            {
                Discovery = await configuration.DiscoverGuildAsync(ConnectionId, GuildId, token);
            }
            catch (Exception exception) when (exception is DiscordApiAuthenticationException or DiscordApiUnavailableException)
            {
                ModelState.AddModelError(string.Empty, "Live Discord server information is unavailable.");
            }
        }

        return true;
    }

    private async Task<DiscordValidatedImage?> ReadImageAsync(CancellationToken token)
    {
        if (UploadedImage is null || UploadedImage.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(RemoteImageUrl))
        {
            throw new DiscordPublicationValidationException("Choose either an uploaded image or a remote image URL, not both.");
        }

        await using Stream source = UploadedImage.OpenReadStream();
        using var memory = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, token);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > DiscordImageValidation.MaximumBytes)
            {
                throw new DiscordPublicationValidationException("The uploaded image must be no larger than 8 MiB.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }

        return DiscordImageValidation.Validate(
            memory.ToArray(),
            Path.GetFileName(UploadedImage.FileName),
            UploadedImage.ContentType,
            ImageAltText,
            ImageSpoiler,
            ImageInEmbed,
            SubmissionId);
    }

    private string CreateReviewToken(Guid actorUserId, DiscordValidatedImage? image)
    {
        byte[] fingerprint = CreateReviewFingerprint(actorUserId, image);
        return dataProtectionProvider
            .CreateProtector(ReviewProtectionPurpose)
            .ToTimeLimitedDataProtector()
            .Protect(Convert.ToBase64String(fingerprint), ReviewLifetime);
    }

    private bool ReviewMatches(Guid actorUserId, DiscordValidatedImage? image)
    {
        if (!ReviewComplete || string.IsNullOrEmpty(ReviewToken))
        {
            return false;
        }

        try
        {
            string protectedFingerprint = dataProtectionProvider
                .CreateProtector(ReviewProtectionPurpose)
                .ToTimeLimitedDataProtector()
                .Unprotect(ReviewToken);
            byte[] expected = Convert.FromBase64String(protectedFingerprint);
            byte[] actual = CreateReviewFingerprint(actorUserId, image);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private byte[] CreateReviewFingerprint(Guid actorUserId, DiscordValidatedImage? image)
    {
        string? imageDigest = image is null
            ? null
            : Convert.ToBase64String(SHA256.HashData(image.Bytes));
        var reviewed = new
        {
            ActorUserId = actorUserId,
            SubmissionId,
            AnnouncementId = Id,
            AnnouncementRevision,
            ConnectionId,
            GuildId,
            DestinationIds = DestinationIds.Order().ToArray(),
            Mode,
            PlainContent,
            ShowLinkPreviews,
            EmbedMessageText,
            EmbedTitle,
            EmbedDescription,
            EmbedTitleUrl,
            EmbedColor,
            EmbedFooter,
            EmbedImageUrl,
            EmbedThumbnailUrl,
            RemoteImageUrl,
            ImageAltText,
            ImageSpoiler,
            ImageInEmbed,
            ImageDigest = imageDigest,
            MentionEveryone,
            MentionHere,
            RoleIds = RoleIds.Order(StringComparer.Ordinal).ToArray(),
            UserIds = UserIds.Order(StringComparer.Ordinal).ToArray(),
            ManualUserId,
            MassMentionConfirmed,
        };
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(reviewed));
    }
}
