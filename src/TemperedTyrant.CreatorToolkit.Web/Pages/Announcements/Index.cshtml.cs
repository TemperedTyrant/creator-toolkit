using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class IndexModel(IAnnouncementService announcementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = nameof(AnnouncementStatusFilter.Draft);

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public string? Notice { get; set; }

    public AnnouncementPage Results { get; private set; } = new(
        [],
        string.Empty,
        AnnouncementStatusFilter.Draft,
        1,
        AnnouncementPage.DefaultPageSize,
        0,
        1);

    public bool CanEdit => AnnouncementPageUser.CanEdit(User);

    public string? StatusMessage => Notice switch
    {
        "deleted" => "The announcement was permanently deleted.",
        _ => null,
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        AnnouncementStatusFilter filter = Enum.TryParse(
            Status,
            ignoreCase: true,
            out AnnouncementStatusFilter parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : AnnouncementStatusFilter.Draft;
        Results = await announcementService.ListAsync(
            new AnnouncementListRequest(
                Search,
                filter,
                PageNumber,
                AnnouncementPage.DefaultPageSize),
            cancellationToken);
        Search = Results.Search;
        Status = Results.Status.ToString();
        PageNumber = Results.Page;
    }
}
