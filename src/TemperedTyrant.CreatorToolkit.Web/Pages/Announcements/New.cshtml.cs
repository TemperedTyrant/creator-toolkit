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
public sealed class NewModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty]
    public Guid AnnouncementId { get; set; }

    [BindProperty]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    public string MessageContent { get; set; } = string.Empty;

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

    public void OnGet()
    {
        AnnouncementId = Guid.NewGuid();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = AnnouncementPageUser.GetActorUserId(User);
        if (actorUserId is null)
        {
            return Forbid();
        }

        if (AnnouncementId == Guid.Empty)
        {
            ModelState.AddModelError(string.Empty, "The draft could not be created. Reload the page and try again.");
            return Page();
        }

        IReadOnlyList<AnnouncementMediaUpload> uploads = [];
        try
        {
            uploads = await AnnouncementMediaForm.ReadUploadsAsync(
                NewImages,
                NewImageAltTexts,
                NewImageSpoilers,
                NewImagePresentations,
                NewImageSortOrders,
                0,
                cancellationToken);
            AnnouncementOperationResult result = await announcementService.CreateAsync(
                AnnouncementId,
                Title,
                MessageContent,
                actorUserId.Value,
                uploads,
                cancellationToken);
            if (result.Status is AnnouncementOperationStatus.Succeeded
                or AnnouncementOperationStatus.DuplicateSubmission)
            {
                return RedirectToPage(
                    "/Announcements/Details",
                    new { id = result.AnnouncementId, notice = "created" });
            }

            if (result.Status == AnnouncementOperationStatus.ValidationFailed)
            {
                AddValidationErrors(result.ValidationErrors);
                return Page();
            }

            ModelState.AddModelError(string.Empty, "The draft could not be created.");
            return Page();
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
    }

    private void AddValidationErrors(
        IReadOnlyList<AnnouncementValidationError> errors)
    {
        foreach (AnnouncementValidationError error in errors)
        {
            ModelState.AddModelError(error.Field, error.Message);
        }
    }
}
