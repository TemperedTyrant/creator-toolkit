using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Web.RateLimiting;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Login)]
[SensitiveSecurityHeaderProfile]
public sealed class LoginModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter) : PageModel
{
    private const string GenericFailure = "The username or password is incorrect.";

    [BindProperty]
    [Required]
    [Display(Name = "Username")]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Dashboard");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ClearPassword();
            return Page();
        }

        ApplicationUser? user = await userManager.FindByNameAsync(UserName);
        if (user is null)
        {
            await AuditRejectionAsync(
                null,
                AuditReasonCode.InvalidCredentials,
                cancellationToken);
            ModelState.AddModelError(string.Empty, GenericFailure);
            ClearPassword();
            return Page();
        }

        if (!user.IsEnabled)
        {
            await AuditRejectionAsync(
                user.Id,
                AuditReasonCode.Disabled,
                cancellationToken);
            ModelState.AddModelError(string.Empty, GenericFailure);
            ClearPassword();
            return Page();
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Microsoft.AspNetCore.Identity.SignInResult result =
            await signInManager.PasswordSignInAsync(
            user,
            Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            await auditWriter.WriteAsync(
                new AuditEvent(
                    AuditEventCode.LoginRejected,
                    AuditOutcome.Rejected,
                    TargetUserId: user.Id,
                    ReasonCode: result.IsLockedOut
                        ? AuditReasonCode.LockedOut
                        : AuditReasonCode.InvalidCredentials),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, GenericFailure);
            ClearPassword();
            return Page();
        }

        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.LoginSucceeded,
                AuditOutcome.Succeeded,
                ActorUserId: user.Id,
                TargetUserId: user.Id),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Dashboard");
    }

    private void ClearPassword()
    {
        Password = string.Empty;
        ModelState.Remove(nameof(Password));
    }

    private async Task AuditRejectionAsync(
        Guid? targetUserId,
        AuditReasonCode reasonCode,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.LoginRejected,
                AuditOutcome.Rejected,
                TargetUserId: targetUserId,
                ReasonCode: reasonCode),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
