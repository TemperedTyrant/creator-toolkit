using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.RateLimiting;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.OwnerRecovery)]
[CapabilitySecurityHeaderProfile]
public sealed class RecoverOwnerModel(OwnerRecoveryService recoveryService) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(256)]
    public string Capability { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ClearSecrets();
            return Page();
        }

        OwnerRecoveryResult result = await recoveryService.CompleteAsync(
            Capability,
            NewPassword,
            cancellationToken);
        if (result.Status == OwnerRecoveryStatus.Succeeded)
        {
            return RedirectToPage("/Login");
        }

        if (result.Status == OwnerRecoveryStatus.Invalid)
        {
            ModelState.AddModelError(
                string.Empty,
                "The Owner recovery capability is invalid or expired.");
        }
        else
        {
            foreach (string error in result.ValidationErrors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
        }

        ClearSecrets();
        return Page();
    }

    private void ClearSecrets()
    {
        Capability = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Capability));
        ModelState.Remove(nameof(NewPassword));
        ModelState.Remove(nameof(ConfirmPassword));
    }
}
