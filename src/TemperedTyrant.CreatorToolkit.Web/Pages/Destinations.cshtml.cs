using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class DestinationsModel(IDiscordConfigurationService discord) : PageModel
{
    public IReadOnlyList<DiscordConnectionListItem> Connections { get; private set; } = [];

    public bool CanManage => User.IsInRole(SystemRoles.Owner)
        || User.IsInRole(SystemRoles.Admin);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Connections = await discord.ListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostEnableAsync(
        Guid id,
        long revision,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!CanManage)
        {
            return Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordOperationResult result = await discord.SetConnectionEnabledAsync(
            id,
            revision,
            enabled,
            actor.Value,
            cancellationToken);
        return RedirectToPage(new { notice = result.Status.ToString().ToLowerInvariant() });
    }
}
