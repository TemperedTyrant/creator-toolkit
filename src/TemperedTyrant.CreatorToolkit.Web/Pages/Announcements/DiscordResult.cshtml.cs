using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Discord;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ContentEditing)]
[SensitiveSecurityHeaderProfile]
public sealed class DiscordResultModel(DiscordPublicationResultStore resultStore) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid SubmissionId { get; set; }

    public DiscordPublicationResult? Result { get; private set; }

    public IActionResult OnGet()
    {
        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        Result = resultStore.Take(actor.Value, SubmissionId);
        return Result is null ? NotFound() : Page();
    }
}
