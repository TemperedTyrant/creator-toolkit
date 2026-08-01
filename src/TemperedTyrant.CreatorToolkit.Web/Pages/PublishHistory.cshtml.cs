using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class PublishHistoryModel(IPublicationHistoryService publications) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public PublicationStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public PublicationProvider? Provider { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? RequestedFromUtc { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? RequestedToUtc { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PublicationHistoryPage Results { get; private set; } = new([], 1, 25, 0, 1);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Results = await publications.ListAsync(
            new PublicationHistoryRequest(
                Status,
                Provider,
                RequestedFromUtc,
                RequestedToUtc,
                PageNumber),
            cancellationToken);
    }
}
