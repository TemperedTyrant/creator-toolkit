using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.Web.RateLimiting;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Setup)]
[SetupSecurityHeaderProfile]
public sealed class SetupModel(
    CreatorToolkitDbContext dbContext,
    InitialOwnerSetupService setupService) : PageModel
{
    [BindProperty]
    [Required]
    [Display(Name = "Bootstrap token")]
    public string Capability { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [StringLength(256)]
    [Display(Name = "Username")]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(200)]
    [Display(Name = "Display name (optional)")]
    public string? DisplayName { get; set; }

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(Password))]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await IsInitializedAsync(cancellationToken)
            ? NotFound()
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await IsInitializedAsync(cancellationToken))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            ClearSecretsBeforeRendering();
            return Page();
        }

        InitialOwnerSetupResult result = await setupService.CreateAsync(
            new InitialOwnerSetupRequest(
                Capability,
                UserName,
                DisplayName,
                Password),
            cancellationToken);

        if (result.Status == InitialOwnerSetupStatus.Succeeded)
        {
            return RedirectToPage("/Login");
        }

        if (result.Status == InitialOwnerSetupStatus.AlreadyInitialized)
        {
            return NotFound();
        }

        if (result.Status == InitialOwnerSetupStatus.InvalidCapability)
        {
            ModelState.AddModelError(
                string.Empty,
                "The bootstrap token is invalid or expired.");
        }
        else
        {
            foreach (string error in result.ValidationErrors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
        }

        ClearSecretsBeforeRendering();
        return Page();
    }

    private Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        return dbContext.InstallationStates.AnyAsync(
            state =>
                state.Id == InstallationState.SingletonId
                && state.InitializedAtUtc != null,
            cancellationToken);
    }

    private void ClearSecretsBeforeRendering()
    {
        Capability = string.Empty;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Capability));
        ModelState.Remove(nameof(Password));
        ModelState.Remove(nameof(ConfirmPassword));
    }
}
