using System.Net;

namespace TemperedTyrant.CreatorToolkit.Web.Configuration;

public static class CreatorToolkitOptionsValidator
{
    public static CreatorToolkitOptions GetValidated(
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        string configuredDataDirectory = configuration["DataDirectory"] ?? "data";
        if (string.IsNullOrWhiteSpace(configuredDataDirectory))
        {
            throw new InvalidOperationException("DataDirectory must not be empty.");
        }

        string dataDirectory;
        try
        {
            dataDirectory = Path.GetFullPath(configuredDataDirectory, contentRootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException)
        {
            throw new InvalidOperationException("DataDirectory is not a valid portable path.");
        }
        string? configuredPublicUrl = configuration["PublicUrl"];
        Uri? publicUrl = ValidatePublicUrl(configuredPublicUrl);
        IReadOnlyList<IPAddress> trustedProxies = ValidateTrustedProxies(configuration);
        IReadOnlyList<IPNetwork> trustedNetworks = ValidateTrustedNetworks(configuration);

        return new CreatorToolkitOptions(
            dataDirectory,
            publicUrl,
            trustedProxies,
            trustedNetworks);
    }

    private static Uri? ValidatePublicUrl(string? configuredPublicUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredPublicUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(configuredPublicUrl, UriKind.Absolute, out Uri? publicUrl)
            || !string.IsNullOrEmpty(publicUrl.UserInfo)
            || !string.IsNullOrEmpty(publicUrl.Query)
            || !string.IsNullOrEmpty(publicUrl.Fragment))
        {
            throw new InvalidOperationException(
                "PublicUrl must be an absolute URL without credentials, a query, or a fragment.");
        }

        bool isHttps = string.Equals(publicUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool isLoopbackDevelopmentUrl =
            string.Equals(publicUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && publicUrl.IsLoopback;

        if (!isHttps && !isLoopbackDevelopmentUrl)
        {
            throw new InvalidOperationException(
                "PublicUrl must use HTTPS except for an explicit loopback development URL.");
        }

        return publicUrl;
    }

    private static List<IPAddress> ValidateTrustedProxies(
        IConfiguration configuration)
    {
        List<IPAddress> proxies = [];
        foreach (IConfigurationSection child in configuration
            .GetSection("TrustedProxies")
            .GetChildren())
        {
            if (!IPAddress.TryParse(child.Value, out IPAddress? address))
            {
                throw new InvalidOperationException(
                    "Each TrustedProxies entry must be an IP address.");
            }

            proxies.Add(address);
        }

        return proxies;
    }

    private static List<IPNetwork> ValidateTrustedNetworks(
        IConfiguration configuration)
    {
        List<IPNetwork> networks = [];
        foreach (IConfigurationSection child in configuration
            .GetSection("TrustedNetworks")
            .GetChildren())
        {
            if (!IPNetwork.TryParse(child.Value, out IPNetwork network))
            {
                throw new InvalidOperationException(
                    "Each TrustedNetworks entry must use CIDR notation.");
            }

            networks.Add(network);
        }

        return networks;
    }
}
