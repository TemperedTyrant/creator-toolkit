using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.PublishHistory;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class DetailsModel(IPublicationHistoryService publications) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public long Revision { get; set; }

    public PublicationHistoryDetails? Details { get; private set; }

    public bool CanCancel => User.IsInRole(SystemRoles.Owner)
        || User.IsInRole(SystemRoles.Admin)
        || User.IsInRole(SystemRoles.Editor);

    public string CorrectiveAction(string? outcome) => outcome switch
    {
        "success" => "Discord accepted the message.",
        "rate-limited" => "Creator Toolkit will retry at the displayed time.",
        "discord-unavailable" or "connection-failure" or "timed-out" =>
            "Creator Toolkit retries transient failures automatically within the attempt limit.",
        "authentication-failed" => "Ask an Owner or Admin to replace and validate the bot token.",
        "missing-permission" => "Review the bot's effective permissions for this channel.",
        "destination-unavailable" => "Refresh or replace the saved channel destination.",
        "validation-rejected" => "Review the Discord destination and mention configuration.",
        "protected-payload-invalid" => "The protected pending payload could not be processed safely.",
        "cancelled" => "This remaining send was cancelled.",
        _ => "Review the destination state and diagnostic reference before taking action.",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Details = await publications.GetAsync(Id, cancellationToken);
        return Details is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken cancellationToken)
    {
        if (!CanCancel)
        {
            return Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        PublicationCancellationResult result = await publications.CancelAsync(
            Id,
            Revision,
            actor.Value,
            cancellationToken);
        return result switch
        {
            PublicationCancellationResult.Succeeded => RedirectToPage(new { id = Id, notice = "cancelled" }),
            PublicationCancellationResult.NotFound => NotFound(),
            PublicationCancellationResult.StaleRevision => await ConflictAsync(
                "The publication changed. Reload before requesting cancellation.",
                cancellationToken),
            _ => await ConflictAsync(
                "Cancellation was already requested or this publication is complete.",
                cancellationToken),
        };
    }

    private async Task<IActionResult> ConflictAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ModelState.AddModelError(string.Empty, message);
        Details = await publications.GetAsync(Id, cancellationToken);
        return Details is null ? NotFound() : Page();
    }
}
