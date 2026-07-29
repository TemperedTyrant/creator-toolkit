using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Account;

[Authorize(Policy = AuthorizationPolicies.TransferOwnership)]
[SensitiveSecurityHeaderProfile]
public sealed class TransferOwnershipModel(
    OwnershipTransferService transferService,
    CreatorToolkitDbContext dbContext) : PageModel
{
    [BindProperty]
    public Guid TargetUserId { get; set; }

    [BindProperty]
    public long ExpectedOwnershipRevision { get; set; }

    [BindProperty]
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    public IReadOnlyList<OwnershipTarget> Targets { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(cancellationToken) ? Page() : Forbid();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        if (actorUserId is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            ClearPassword();
            await LoadAsync(cancellationToken);
            return Page();
        }

        string? targetStamp = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == TargetUserId)
            .Select(user => user.ConcurrencyStamp)
            .SingleOrDefaultAsync(cancellationToken);
        if (targetStamp is null)
        {
            return NotFound();
        }

        OwnershipTransferResult result = await transferService.TransferAsync(
            actorUserId.Value,
            TargetUserId,
            CurrentPassword,
            ExpectedOwnershipRevision,
            targetStamp,
            cancellationToken);
        ClearPassword();
        if (result.Status == OwnershipTransferStatus.Succeeded)
        {
            return RedirectToPage("/Login");
        }

        if (result.Status == OwnershipTransferStatus.Forbidden)
        {
            return Forbid();
        }

        ModelState.AddModelError(
            string.Empty,
            result.Status switch
            {
                OwnershipTransferStatus.InvalidPassword =>
                    "Ownership transfer could not be verified.",
                OwnershipTransferStatus.Conflict =>
                    "Ownership or the target account changed. Reload and try again.",
                _ => "The selected account is not eligible for ownership.",
            });
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Guid? actorUserId = GetActorUserId();
        if (actorUserId is null)
        {
            return false;
        }

        Targets = await transferService.GetEligibleTargetsAsync(
            actorUserId.Value,
            cancellationToken);
        ExpectedOwnershipRevision = await dbContext.Ownerships
            .AsNoTracking()
            .Select(ownership => ownership.Revision)
            .SingleAsync(cancellationToken);
        return true;
    }

    private void ClearPassword()
    {
        CurrentPassword = string.Empty;
        ModelState.Remove(nameof(CurrentPassword));
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
