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
public sealed class ManageUserModel(
    UserLifecycleService lifecycleService,
    CreatorToolkitOptions options) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid UserId { get; set; }

    [BindProperty]
    public string ExpectedConcurrencyStamp { get; set; } = string.Empty;

    [BindProperty]
    public string NewRole { get; set; } = string.Empty;

    public ManageableUser? Target { get; private set; }

    public string? OneTimeActivationLink { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await LoadTargetAsync(cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRoleAsync(CancellationToken cancellationToken)
    {
        UserLifecycleResult? result = await InvokeAsync(
            (actor, token) => lifecycleService.ChangeRoleAsync(
                actor,
                UserId,
                ExpectedConcurrencyStamp,
                NewRole,
                token),
            cancellationToken);
        return await HandleMutationResultAsync(
            result,
            redirectOnSuccess: true,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken)
    {
        UserLifecycleResult? result = await InvokeAsync(
            (actor, token) => lifecycleService.DisableAsync(
                actor,
                UserId,
                ExpectedConcurrencyStamp,
                token),
            cancellationToken);
        return await HandleMutationResultAsync(
            result,
            redirectOnSuccess: true,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        UserLifecycleResult? result = await InvokeAsync(
            (actor, token) => lifecycleService.DeleteAsync(
                actor,
                UserId,
                ExpectedConcurrencyStamp,
                token),
            cancellationToken);
        if (result?.Status == UserLifecycleStatus.Succeeded)
        {
            return RedirectToPage("/Account/CreateUser");
        }

        return await HandleMutationResultAsync(
            result,
            redirectOnSuccess: false,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostRegenerateActivationAsync(
        CancellationToken cancellationToken)
    {
        UserLifecycleResult? result = await InvokeAsync(
            (actor, token) => lifecycleService.RegenerateActivationAsync(
                actor,
                UserId,
                ExpectedConcurrencyStamp,
                token),
            cancellationToken);
        if (result?.Status == UserLifecycleStatus.Succeeded
            && result.OneTimeActivationCapability is not null)
        {
            OneTimeActivationLink = BuildActivationLink(
                result.OneTimeActivationCapability);
            if (!await LoadTargetAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        return await HandleMutationResultAsync(
            result,
            redirectOnSuccess: false,
            cancellationToken);
    }

    private async Task<UserLifecycleResult?> InvokeAsync(
        Func<Guid, CancellationToken, Task<UserLifecycleResult>> operation,
        CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        return actorUserId is null
            ? null
            : await operation(actorUserId.Value, cancellationToken);
    }

    private async Task<IActionResult> HandleMutationResultAsync(
        UserLifecycleResult? result,
        bool redirectOnSuccess,
        CancellationToken cancellationToken)
    {
        if (result is null || result.Status == UserLifecycleStatus.Forbidden)
        {
            return Forbid();
        }

        if (result.Status == UserLifecycleStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == UserLifecycleStatus.Succeeded && redirectOnSuccess)
        {
            return RedirectToPage(new { userId = UserId });
        }

        string message = result.Status switch
        {
            UserLifecycleStatus.Conflict =>
                "The account changed. Reload the page and try again.",
            UserLifecycleStatus.SoleOwnerProtected =>
                "The current Owner can be changed only through ownership transfer.",
            UserLifecycleStatus.InvalidState =>
                "The account is not in a valid state for that operation.",
            _ => "The account operation could not be completed.",
        };
        ModelState.AddModelError(string.Empty, message);
        await LoadTargetAsync(cancellationToken);
        return Page();
    }

    private async Task<bool> LoadTargetAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        if (actorUserId is null)
        {
            return false;
        }

        Target = await lifecycleService.GetManageableUserAsync(
            actorUserId.Value,
            UserId,
            cancellationToken);
        if (Target is null)
        {
            return false;
        }

        ExpectedConcurrencyStamp = Target.ConcurrencyStamp;
        NewRole = Target.Role;
        return true;
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
