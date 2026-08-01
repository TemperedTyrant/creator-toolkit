using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ContentEditing)]
[SensitiveScriptSecurityHeaderProfile]
[RequestSizeLimit(9 * 1024 * 1024)]
public sealed class EditModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    public string MessageContent { get; set; } = string.Empty;

    [BindProperty]
    public long Revision { get; set; }

    [BindProperty]
    public List<AnnouncementMediaEditInput> ExistingMedia { get; set; } = [];

    [BindProperty]
    public List<IFormFile> NewImages { get; set; } = [];

    [BindProperty]
    public List<string?> NewImageAltTexts { get; set; } = [];

    [BindProperty]
    public List<bool> NewImageSpoilers { get; set; } = [];

    [BindProperty]
    public List<AnnouncementMediaPresentation> NewImagePresentations { get; set; } = [];

    [BindProperty]
    public List<int> NewImageSortOrders { get; set; } = [];

    public bool HasConflict { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        AnnouncementDetails? item = await announcementService.GetAsync(Id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status == AnnouncementStatus.Archived)
        {
            return RedirectToPage(
                "/Announcements/Details",
                new { id = Id, notice = "readonly" });
        }

        Title = item.Title;
        MessageContent = item.MessageContent;
        Revision = item.Revision;
        ExistingMedia = item.Media.Select(value => new AnnouncementMediaEditInput
        {
            Id = value.Id,
            Revision = value.Revision,
            SortOrder = value.SortOrder,
            AltText = value.AltText,
            IsSpoiler = value.IsSpoiler,
            Presentation = value.Presentation,
            ContentType = value.ContentType,
            ByteLength = value.ByteLength,
        }).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = AnnouncementPageUser.GetActorUserId(User);
        if (actorUserId is null)
        {
            return Forbid();
        }

        IReadOnlyList<AnnouncementMediaUpload> uploads = [];
        AnnouncementOperationResult result;
        try
        {
            int retained = ExistingMedia.Count(value => !value.Remove);
            uploads = await AnnouncementMediaForm.ReadUploadsAsync(
                NewImages,
                NewImageAltTexts,
                NewImageSpoilers,
                NewImagePresentations,
                NewImageSortOrders,
                retained,
                cancellationToken);
            result = await announcementService.UpdateAsync(
                Id,
                Title,
                MessageContent,
                Revision,
                actorUserId.Value,
                new AnnouncementMediaChangeSet(
                    ExistingMedia.Select(value => value.ToDomain()).ToArray(),
                    uploads),
                cancellationToken);
        }
        catch (AnnouncementMediaFormException exception)
        {
            ModelState.AddModelError("Media", exception.Message);
            return Page();
        }
        finally
        {
            AnnouncementMediaForm.Zero(uploads);
        }
        if (result.Status == AnnouncementOperationStatus.Succeeded)
        {
            return RedirectToPage(
                "/Announcements/Details",
                new { id = Id, notice = "updated" });
        }

        if (result.Status == AnnouncementOperationStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == AnnouncementOperationStatus.StaleRevision)
        {
            HasConflict = true;
            ModelState.AddModelError(
                string.Empty,
                "This draft changed after you opened it. Your entered title and content are preserved below; reload the current draft before trying again.");
            return Page();
        }

        if (result.Status == AnnouncementOperationStatus.InvalidTransition)
        {
            return RedirectToPage(
                "/Announcements/Details",
                new { id = Id, notice = "readonly" });
        }

        if (result.Status == AnnouncementOperationStatus.ValidationFailed)
        {
            foreach (AnnouncementValidationError error in result.ValidationErrors)
            {
                ModelState.AddModelError(error.Field, error.Message);
            }

            return Page();
        }

        ModelState.AddModelError(string.Empty, "The draft could not be updated.");
        return Page();
    }
}
