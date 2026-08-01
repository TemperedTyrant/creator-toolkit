using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Destinations.Discord;

[Authorize(Policy = AuthorizationPolicies.Administration)]
[SensitiveSecurityHeaderProfile]
[RequestSizeLimit(64 * 1024)]
public sealed class NewModel(IDiscordConfigurationService discord) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string BotToken { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (!ModelState.IsValid || actor is null)
        {
            BotToken = string.Empty;
            ModelState.Remove(nameof(BotToken));
            return actor is null ? Forbid() : Page();
        }

        DiscordOperationResult result = await discord.CreateAsync(
            Name,
            BotToken,
            actor.Value,
            cancellationToken);
        BotToken = string.Empty;
        ModelState.Remove(nameof(BotToken));
        if (result.Status == DiscordOperationStatus.Succeeded)
        {
            return RedirectToPage("/Destinations/Discord/Details", new { id = result.Id, notice = "created" });
        }

        ModelState.AddModelError(
            nameof(BotToken),
            result.Status == DiscordOperationStatus.AuthenticationFailed
                ? "Discord did not accept that bot credential. Verify the bot token and try again."
                : "The Discord bot could not be validated right now.");
        return Page();
    }
}
