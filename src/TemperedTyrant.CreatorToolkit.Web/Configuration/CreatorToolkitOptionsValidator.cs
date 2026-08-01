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
        foreach (string value in GetConfiguredValues(configuration, "TrustedProxies"))
        {
            if (!IPAddress.TryParse(value, out IPAddress? address))
            {
                throw new InvalidOperationException(
                    "Each TrustedProxies entry must be an IP address.");
            }

            if (!proxies.Contains(address))
            {
                proxies.Add(address);
            }
        }

        return proxies;
    }

    private static List<IPNetwork> ValidateTrustedNetworks(
        IConfiguration configuration)
    {
        List<IPNetwork> networks = [];
        foreach (string value in GetConfiguredValues(configuration, "TrustedNetworks"))
        {
            if (!IPNetwork.TryParse(value, out IPNetwork network))
            {
                throw new InvalidOperationException(
                    "Each TrustedNetworks entry must use CIDR notation.");
            }

            if (!networks.Contains(network))
            {
                networks.Add(network);
            }
        }

        return networks;
    }

    private static IEnumerable<string> GetConfiguredValues(
        IConfiguration configuration,
        string key)
    {
        IConfigurationSection section = configuration.GetSection(key);
        IConfigurationSection[] children = [.. section.GetChildren()];
        if (children.Length > 0)
        {
            return children
                .Select(child => child.Value?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0);
        }

        return (section.Value ?? string.Empty).Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
