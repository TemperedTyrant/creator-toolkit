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

        return new CreatorToolkitOptions(dataDirectory, publicUrl);
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
}
