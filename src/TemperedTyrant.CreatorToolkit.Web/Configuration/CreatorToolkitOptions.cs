using System.Net;

namespace TemperedTyrant.CreatorToolkit.Web.Configuration;

public sealed record CreatorToolkitOptions(
    string DataDirectory,
    Uri? PublicUrl,
    IReadOnlyList<IPAddress> TrustedProxies,
    IReadOnlyList<IPNetwork> TrustedNetworks);
