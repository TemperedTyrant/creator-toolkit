using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.ReadModels;
using TemperedTyrant.CreatorToolkit.Web.Authorization;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
public sealed class DashboardModel(ApplicationShellQueryService queryService) : PageModel
{
    public DashboardState State { get; private set; } = null!;

    [Microsoft.AspNetCore.Mvc.TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Guid userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        State = await queryService.GetDashboardAsync(userId, cancellationToken);
    }
}
