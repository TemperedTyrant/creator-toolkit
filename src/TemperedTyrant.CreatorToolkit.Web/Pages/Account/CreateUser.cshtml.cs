using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Account;

[Authorize(Policy = AuthorizationPolicies.ManageUsers)]
[SensitiveSecurityHeaderProfile]
public sealed class CreateUserModel(
    UserLifecycleService lifecycleService,
    CreatorToolkitOptions options) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(256)]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(200)]
    public string? DisplayName { get; set; }

    [BindProperty]
    [Required]
    public string Role { get; set; } = string.Empty;

    public IReadOnlyList<string> AvailableRoles { get; private set; } = [];

    public string? OneTimeActivationLink { get; private set; }

    public Guid? CreatedUserId { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await LoadRolesAsync(cancellationToken) ? Page() : Forbid();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        if (actorUserId is null || !await LoadRolesAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        UserLifecycleResult result = await lifecycleService.CreatePendingAsync(
            actorUserId.Value,
            UserName,
            DisplayName,
            Role,
            cancellationToken);
        if (result.Status == UserLifecycleStatus.Forbidden)
        {
            return Forbid();
        }

        if (result.Status == UserLifecycleStatus.ValidationFailed)
        {
            foreach (string error in result.ValidationErrors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return Page();
        }

        if (result.Status != UserLifecycleStatus.Succeeded
            || result.OneTimeActivationCapability is null)
        {
            ModelState.AddModelError(string.Empty, "The pending account could not be created.");
            return Page();
        }

        OneTimeActivationLink = BuildActivationLink(result.OneTimeActivationCapability);
        CreatedUserId = result.TargetUserId;
        return Page();
    }

    private async Task<bool> LoadRolesAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        if (actorUserId is null)
        {
            return false;
        }

        AvailableRoles = await lifecycleService.GetCreatableRolesAsync(
            actorUserId.Value,
            cancellationToken);
        return AvailableRoles.Count > 0;
    }

    private string BuildActivationLink(string capability)
    {
        const string route = "/Account/Activate";
        return options.PublicUrl is null
            ? $"{route}#token={capability}"
            : $"{new Uri(options.PublicUrl, route).AbsoluteUri}#token={capability}";
    }

    private Guid? GetActorUserId()
    {
        return Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out Guid actorUserId)
            ? actorUserId
            : null;
    }
}
