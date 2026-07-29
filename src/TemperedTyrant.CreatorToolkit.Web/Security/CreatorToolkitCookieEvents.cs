using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

namespace TemperedTyrant.CreatorToolkit.Web.Security;

public sealed class CreatorToolkitCookieEvents(
    ISecurityStampValidator securityStampValidator,
    UserManager<ApplicationUser> userManager) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await securityStampValidator.ValidateAsync(context);
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        ApplicationUser? user = await userManager.GetUserAsync(context.Principal);
        if (user?.IsEnabled == true)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
