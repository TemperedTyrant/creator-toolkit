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
public sealed class DeleteModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public long Revision { get; set; }

    public AnnouncementDetails? Item { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = AnnouncementPageUser.GetActorUserId(User);
        if (actorUserId is null)
        {
            return Forbid();
        }

        AnnouncementOperationResult result = await announcementService.DeleteAsync(
            Id,
            Revision,
            actorUserId.Value,
            cancellationToken);
        if (result.Status == AnnouncementOperationStatus.Succeeded)
        {
            return RedirectToPage("/Announcements/Index", new { notice = "deleted" });
        }

        if (result.Status == AnnouncementOperationStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == AnnouncementOperationStatus.StaleRevision)
        {
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            ModelState.AddModelError(
                string.Empty,
                "The announcement changed before deletion. Review the current record and confirm again if it should still be deleted.");
            return Page();
        }

        if (result.Status == AnnouncementOperationStatus.InvalidTransition)
        {
            ModelState.AddModelError(
                string.Empty,
                "Cancel pending publications and wait for processing to finish before deleting this announcement.");
            return await LoadAsync(cancellationToken) ? Page() : NotFound();
        }

        ModelState.AddModelError(string.Empty, "The announcement could not be deleted.");
        return await LoadAsync(cancellationToken) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Item = await announcementService.GetAsync(Id, cancellationToken);
        if (Item is null)
        {
            return false;
        }

        Revision = Item.Revision;
        return true;
    }
}
