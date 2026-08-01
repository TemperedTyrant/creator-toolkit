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
public sealed class RestoreModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public long Revision { get; set; }

    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = AnnouncementPageUser.GetActorUserId(User);
        if (actorUserId is null)
        {
            return Forbid();
        }

        AnnouncementOperationResult result = await announcementService.RestoreAsync(
            Id,
            Revision,
            actorUserId.Value,
            cancellationToken);
        return result.Status switch
        {
            AnnouncementOperationStatus.Succeeded =>
                RedirectToPage("/Announcements/Details", new { id = Id, notice = "restored" }),
            AnnouncementOperationStatus.StaleRevision =>
                RedirectToPage("/Announcements/Details", new { id = Id, notice = "conflict" }),
            AnnouncementOperationStatus.InvalidTransition =>
                RedirectToPage("/Announcements/Details", new { id = Id, notice = "invalid-state" }),
            AnnouncementOperationStatus.NotFound => NotFound(),
            _ => RedirectToPage("/Announcements/Details", new { id = Id, notice = "invalid-state" }),
        };
    }
}
