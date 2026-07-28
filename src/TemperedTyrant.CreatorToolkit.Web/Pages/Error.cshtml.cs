using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[SensitiveSecurityHeaderProfile]
public sealed class ErrorModel : PageModel
{
    public void OnGet()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
    }
}
