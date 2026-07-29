using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Authorization;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.ManageUsers)]
public sealed class UsersModel(UserLifecycleService lifecycleService) : PageModel
{
    public UserDirectoryResult Directory { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Guid userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        UserDirectoryResult? directory = await lifecycleService.GetUserDirectoryAsync(
            userId,
            cancellationToken);
        if (directory is null)
        {
            return Forbid();
        }

        Directory = directory;
        return Page();
    }
}
