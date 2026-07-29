using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.Web.Pages;

[Authorize(Policy = AuthorizationPolicies.Administration)]
public sealed class SettingsModel(CreatorToolkitOptions options) : PageModel
{
    public bool PublicUrlConfigured { get; } = options.PublicUrl is not null;

    public int TrustedProxyCount { get; } = options.TrustedProxies.Count;

    public int TrustedNetworkCount { get; } = options.TrustedNetworks.Count;
}
