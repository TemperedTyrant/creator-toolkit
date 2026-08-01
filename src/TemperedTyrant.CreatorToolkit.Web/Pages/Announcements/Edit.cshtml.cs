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
public sealed class EditModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public string Title { get; set; } = string.Empty;

    [BindProperty]
    public string Body { get; set; } = string.Empty;

    [BindProperty]
    public long Revision { get; set; }

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
        Body = item.Body;
        Revision = item.Revision;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = AnnouncementPageUser.GetActorUserId(User);
        if (actorUserId is null)
        {
            return Forbid();
        }

        AnnouncementOperationResult result = await announcementService.UpdateAsync(
            Id,
            Title,
            Body,
            Revision,
            actorUserId.Value,
            cancellationToken);
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
