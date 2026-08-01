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
[SensitiveScriptSecurityHeaderProfile]
[RequestSizeLimit(9 * 1024 * 1024)]
public sealed class PublishDiscordModel(
    IDiscordPublishingService publishing,
    IDiscordConfigurationService configuration,
    DiscordEphemeralUploadStore uploadStore,
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

    public string? ReviewImageFormat { get; private set; }

    public int? ReviewImageByteSize { get; private set; }

    public string? ReviewImageSafeFileName { get; private set; }

    public bool ReviewImageHasAltText { get; private set; }

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
        if (Request.Headers["X-Creator-Toolkit-Partial"] == "member-search")
        {
            return await SearchMembersPartialAsync(cancellationToken);
        }

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
        catch (Exception exception) when (
            exception is ArgumentException
                or DiscordApiAuthenticationException
                or DiscordApiUnavailableException
                or DiscordServerInformationException)
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
        ReviewState? previousReview = ReadReviewState();
        if (!string.IsNullOrEmpty(ReviewToken) && previousReview is null)
        {
            uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
        }
        try
        {
            image = await ReadImageAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is DiscordPublicationValidationException or DiscordMessageValidationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }

        DiscordStagedUpload? stagedUpload = null;
        bool imageWasStaged = false;
        DiscordEphemeralUploadBinding binding = UploadBinding(actor.Value);
        try
        {
            if (ModelState.IsValid && Discovery is not null)
            {
                try
                {
                    await publishing.ValidateReviewAsync(
                        CreateRequest(image),
                        CanUseMassMentions,
                        Discovery,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or DiscordPublicationValidationException
                        or DiscordMessageValidationException
                        or DiscordApiAuthenticationException
                        or DiscordApiUnavailableException
                        or DiscordServerInformationException)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        exception is DiscordPublicationValidationException validation
                            ? validation.Message
                            : "The reviewed Discord publication could not be validated safely.");
                }
            }

            if (ModelState.IsValid)
            {
                if (image is not null)
                {
                    uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
                    stagedUpload = uploadStore.Stage(binding, image);
                    imageWasStaged = true;
                }
                else if (previousReview?.UploadHandle is not null)
                {
                    stagedUpload = uploadStore.GetMetadata(previousReview.UploadHandle, binding);
                    if (stagedUpload is null)
                    {
                        ModelState.AddModelError(
                            string.Empty,
                            "The selected image has expired. Return to the composer and select it again.");
                    }
                }
            }
        }
        catch (DiscordEphemeralUploadCapacityException)
        {
            ModelState.AddModelError(
                string.Empty,
                "Image review capacity is temporarily unavailable. Try again shortly.");
        }
        finally
        {
            if (image is not null && !imageWasStaged)
            {
                CryptographicOperations.ZeroMemory(image.Bytes);
            }
        }

        ReviewComplete = ModelState.IsValid;
        if (ReviewComplete)
        {
            SetReviewImage(stagedUpload);
            ReviewToken = CreateReviewToken(actor.Value, stagedUpload);
        }
        else
        {
            uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
            ReviewToken = null;
        }

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

        if (!await LoadContextOnlyAsync(cancellationToken))
        {
            uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
            return NotFound();
        }

        DiscordValidatedImage? stagedImage = null;
        try
        {
            List<string> users = UserIds.ToList();
            if (!string.IsNullOrWhiteSpace(ManualUserId))
            {
                users.Add(ManualUserId.Trim());
            }

            ReviewState? review = ReadReviewState();
            if (review?.UploadHandle is not null)
            {
                SetReviewImage(uploadStore.GetMetadata(
                    review.UploadHandle,
                    UploadBinding(actor.Value)));
            }
            if (UploadedImage is not null || !ReviewMatches(actor.Value, review))
            {
                ReviewComplete = false;
                ReviewToken = null;
                uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
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

            Guid? existingPublication = await publishing.FindEnqueuedAsync(
                SubmissionId,
                actor.Value,
                cancellationToken);
            if (existingPublication is not null)
            {
                uploadStore.RemoveForSubmission(actor.Value, SubmissionId);
                return RedirectToPage(
                    "/PublishHistory/Details",
                    new { id = existingPublication.Value });
            }

            if (review!.UploadHandle is not null)
            {
                stagedImage = uploadStore.Copy(
                    review.UploadHandle,
                    UploadBinding(actor.Value));
                if (stagedImage is null)
                {
                    ReviewComplete = false;
                    ReviewToken = null;
                    ModelState.Remove(nameof(ReviewComplete));
                    ModelState.Remove(nameof(ReviewToken));
                    ModelState.AddModelError(
                        string.Empty,
                        "The selected image has expired. Return to the composer and select it again.");
                    return Page();
                }
            }

            DiscordPublishRequest request = CreateRequest(stagedImage, users);
            DiscordPublicationEnqueueResult result = await publishing.EnqueueAsync(
                request,
                CanUseMassMentions,
                actor.Value,
                cancellationToken);
            if (review.UploadHandle is not null)
            {
                _ = uploadStore.Remove(review.UploadHandle, UploadBinding(actor.Value));
            }

            return RedirectToPage(
                "/PublishHistory/Details",
                new { id = result.PublicationId });
        }
        catch (DiscordPublicationValidationException exception)
        {
            if (stagedImage is not null)
            {
                ReviewComplete = false;
                ReviewToken = null;
            }

            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        finally
        {
            if (stagedImage is not null)
            {
                CryptographicOperations.ZeroMemory(stagedImage.Bytes);
            }
        }
    }

    private DiscordPublishRequest CreateRequest(
        DiscordValidatedImage? image,
        IReadOnlyList<string>? selectedUsers = null)
    {
        List<string> users = selectedUsers?.ToList() ?? UserIds.ToList();
        if (selectedUsers is null && !string.IsNullOrWhiteSpace(ManualUserId))
        {
            users.Add(ManualUserId.Trim());
        }

        return new DiscordPublishRequest(
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
    }

    private async Task<IActionResult> SearchMembersPartialAsync(CancellationToken cancellationToken)
    {
        Context = await publishing.GetContextAsync(Id, cancellationToken);
        bool selectionIsValid = Context is not null
            && Context.Connections.Any(value => value.Id == ConnectionId && value.Enabled)
            && Context.Destinations.Any(value =>
                value.ConnectionId == ConnectionId
                && value.GuildId == GuildId
                && value.Enabled);
        string normalized = (MemberQuery ?? string.Empty).Trim();
        if (!selectionIsValid
            || normalized.EnumerateRunes().Count() is < 2 or > 100)
        {
            return new JsonResult(new
            {
                status = "invalid",
                message = "Enter between 2 and 100 characters and select a configured Discord server.",
                members = Array.Empty<object>(),
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }

        try
        {
            IReadOnlyList<DiscordGuildMember> members = await publishing.SearchMembersAsync(
                ConnectionId,
                GuildId,
                normalized,
                cancellationToken);
            return new JsonResult(new
            {
                status = "ok",
                message = members.Count == 0
                    ? "No matching Discord members were found."
                    : $"Found {members.Count} Discord member(s).",
                members = members.Take(25).Select(value => new
                {
                    id = value.UserId,
                    displayName = value.DisplayName,
                }),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or DiscordApiAuthenticationException
                or DiscordApiUnavailableException
                or DiscordServerInformationException)
        {
            return new JsonResult(new
            {
                status = "unavailable",
                message = "Member search is unavailable. Use a Discord user ID instead.",
                members = Array.Empty<object>(),
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
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
            catch (DiscordServerInformationException exception)
            {
                ModelState.AddModelError(string.Empty, exception.SafeMessage);
            }
            catch (Exception exception) when (exception is DiscordApiAuthenticationException or DiscordApiUnavailableException)
            {
                ModelState.AddModelError(string.Empty, "Live Discord server information is unavailable.");
            }
        }

        return true;
    }

    private async Task<bool> LoadContextOnlyAsync(CancellationToken token)
    {
        Context = await publishing.GetContextAsync(Id, token);
        return Context is not null;
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
        byte[]? validatedBytes = null;
        try
        {
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

            validatedBytes = memory.ToArray();
            DiscordValidatedImage image = DiscordImageValidation.Validate(
                validatedBytes,
                Path.GetFileName(UploadedImage.FileName),
                UploadedImage.ContentType,
                ImageAltText,
                ImageSpoiler,
                ImageInEmbed,
                SubmissionId);
            validatedBytes = null;
            return image;
        }
        finally
        {
            if (validatedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(validatedBytes);
            }

            CryptographicOperations.ZeroMemory(buffer);
            if (memory.TryGetBuffer(out ArraySegment<byte> memoryBuffer))
            {
                CryptographicOperations.ZeroMemory(memoryBuffer.AsSpan());
            }
        }
    }

    private string CreateReviewToken(Guid actorUserId, DiscordStagedUpload? image)
    {
        byte[] fingerprint = CreateReviewFingerprint(actorUserId, image is not null);
        string state = JsonSerializer.Serialize(new ReviewState(
            Convert.ToBase64String(fingerprint),
            image?.Handle));
        return dataProtectionProvider
            .CreateProtector(ReviewProtectionPurpose)
            .ToTimeLimitedDataProtector()
            .Protect(state, ReviewLifetime);
    }

    private bool ReviewMatches(Guid actorUserId, ReviewState? review)
    {
        if (!ReviewComplete || review is null)
        {
            return false;
        }

        try
        {
            byte[] expected = Convert.FromBase64String(review.Fingerprint);
            byte[] actual = CreateReviewFingerprint(
                actorUserId,
                review.UploadHandle is not null);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private ReviewState? ReadReviewState()
    {
        if (string.IsNullOrEmpty(ReviewToken))
        {
            return null;
        }

        try
        {
            string state = dataProtectionProvider
                .CreateProtector(ReviewProtectionPurpose)
                .ToTimeLimitedDataProtector()
                .Unprotect(ReviewToken);
            return JsonSerializer.Deserialize<ReviewState>(state);
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private byte[] CreateReviewFingerprint(Guid actorUserId, bool hasUploadedImage)
    {
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
            HasUploadedImage = hasUploadedImage,
            MentionEveryone,
            MentionHere,
            RoleIds = RoleIds.Order(StringComparer.Ordinal).ToArray(),
            UserIds = UserIds.Order(StringComparer.Ordinal).ToArray(),
            ManualUserId,
            MassMentionConfirmed,
        };
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(reviewed));
    }

    private DiscordEphemeralUploadBinding UploadBinding(Guid actorUserId) => new(
        actorUserId,
        Id,
        AnnouncementRevision,
        ConnectionId,
        GuildId,
        SubmissionId,
        Mode,
        ImageSpoiler,
        ImageInEmbed);

    private void SetReviewImage(DiscordStagedUpload? image)
    {
        ReviewImageFormat = image?.Format;
        ReviewImageByteSize = image?.ByteSize;
        ReviewImageSafeFileName = image?.SafeFileName;
        ReviewImageHasAltText = image?.HasAltText ?? false;
    }

    private sealed record ReviewState(string Fingerprint, string? UploadHandle);
}
