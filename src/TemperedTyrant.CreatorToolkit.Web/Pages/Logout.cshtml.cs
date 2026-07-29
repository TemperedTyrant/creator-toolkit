using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize]
public sealed class LogoutModel(
    SignInManager<ApplicationUser> signInManager,
    CreatorToolkitDbContext dbContext,
    IAuditWriter auditWriter) : PageModel
{
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? userId = Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out Guid parsedUserId)
            ? parsedUserId
            : null;

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await signInManager.SignOutAsync();
        await auditWriter.WriteAsync(
            new AuditEvent(
                AuditEventCode.LogoutSucceeded,
                AuditOutcome.Succeeded,
                ActorUserId: userId,
                TargetUserId: userId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RedirectToPage("/Login");
    }
}
