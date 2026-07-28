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
[EnableRateLimiting(RateLimitPolicies.Activation)]
[CapabilitySecurityHeaderProfile]
public sealed class ActivateModel(AccountActivationService activationService) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(256)]
    public string Capability { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(Password))]
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

        AccountActivationResult result = await activationService.ActivateAsync(
            Capability,
            Password,
            cancellationToken);
        if (result.Status == AccountActivationStatus.Succeeded)
        {
            return RedirectToPage("/Login");
        }

        if (result.Status == AccountActivationStatus.Invalid)
        {
            ModelState.AddModelError(
                string.Empty,
                "The activation capability is invalid or expired.");
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
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Capability));
        ModelState.Remove(nameof(Password));
        ModelState.Remove(nameof(ConfirmPassword));
    }
}
