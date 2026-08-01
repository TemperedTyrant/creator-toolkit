using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ContentEditing)]
[SensitiveSecurityHeaderProfile]
public sealed class NewModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty]
    public Guid AnnouncementId { get; set; }

    [BindProperty]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    public string Body { get; set; } = string.Empty;

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

        AnnouncementOperationResult result = await announcementService.CreateAsync(
            AnnouncementId,
            Title,
            Body,
            actorUserId.Value,
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

    private void AddValidationErrors(
        IReadOnlyList<AnnouncementValidationError> errors)
    {
        foreach (AnnouncementValidationError error in errors)
        {
            ModelState.AddModelError(error.Field, error.Message);
        }
    }
}
