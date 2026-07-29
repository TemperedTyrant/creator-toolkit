using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.Administration)]
public sealed class DebugModel(DebugStatusService debugStatusService) : PageModel
{
    public DebugPageStatus Status { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await debugStatusService.GetAsync(cancellationToken);
    }
}
