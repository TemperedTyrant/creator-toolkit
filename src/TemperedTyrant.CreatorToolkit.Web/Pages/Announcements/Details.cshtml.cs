using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class DetailsModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Notice { get; set; }

    public AnnouncementDetails? Item { get; private set; }

    public bool CanEdit => AnnouncementPageUser.CanEdit(User);

    public string? StatusMessage => Notice switch
    {
        "created" => "The announcement draft was created.",
        "updated" => "The announcement draft was updated.",
        "archived" => "The announcement was archived and is now read-only.",
        "restored" => "The announcement was restored to Draft.",
        "conflict" => "The announcement changed before this operation completed. Review the current version and try again.",
        "readonly" => "Archived announcements are read-only. Restore this announcement before editing it.",
        "invalid-state" => "The announcement is not in a valid state for that operation.",
        _ => null,
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Item = await announcementService.GetAsync(Id, cancellationToken);
        return Item is null ? NotFound() : Page();
    }
}
