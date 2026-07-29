using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Web.Authorization;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
public sealed class ChangePasswordModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter) : PageModel
{
    [BindProperty]
    [Required]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword))]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ClearPasswords();
            return Page();
        }

        ApplicationUser? user = await userManager.GetUserAsync(User);
        if (user is null || !user.IsEnabled)
        {
            return Challenge();
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        IdentityResult result = await userManager.ChangePasswordAsync(
            user,
            CurrentPassword,
            NewPassword);
        if (!result.Succeeded)
        {
            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.PasswordChangeRejected,
                    AuditOutcome.Rejected,
                    ActorUserId: user.Id,
                    TargetUserId: user.Id,
                    ReasonCode: result.Errors.Any(error => error.Code == "PasswordMismatch")
                        ? AuditReasonCode.InvalidCredentials
                        : AuditReasonCode.ValidationFailed),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (string error in result.Errors
                .Select(ToSafeValidationMessage)
                .Distinct())
            {
                ModelState.AddModelError(string.Empty, error);
            }

            ClearPasswords();
            return Page();
        }

        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.PasswordChanged,
                AuditOutcome.Succeeded,
                ActorUserId: user.Id,
                TargetUserId: user.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await signInManager.RefreshSignInAsync(user);

        TempData["StatusMessage"] = "Your password was changed.";
        return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Dashboard");
    }

    private static string ToSafeValidationMessage(IdentityError error)
    {
        return error.Code switch
        {
            "PasswordMismatch" => "The current password is incorrect.",
            "PasswordTooShort" or "PasswordTooLong" or "PasswordCommon"
                or "PasswordInvalidUnicode" or "PasswordRequiresUnique" => error.Description,
            _ => "The password could not be changed.",
        };
    }

    private void ClearPasswords()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(CurrentPassword));
        ModelState.Remove(nameof(NewPassword));
        ModelState.Remove(nameof(ConfirmPassword));
    }
}
