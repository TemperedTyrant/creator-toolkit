using Microsoft.Extensions.Configuration;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class ConfigurationTests
{
    [Fact]
    public void PublicUrlIsOptionalAndDataDirectoryIsPortable()
    {
        using TestDataDirectory root = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataDirectory"] = "local-data",
                })
            .Build();

        CreatorToolkitOptions options =
            CreatorToolkitOptionsValidator.GetValidated(configuration, root.Path);

        Assert.Equal(Path.Combine(root.Path, "local-data"), options.DataDirectory);
        Assert.Null(options.PublicUrl);
        Assert.Empty(options.TrustedProxies);
        Assert.Empty(options.TrustedNetworks);
    }

    [Theory]
    [InlineData("https://example.invalid", "https://example.invalid/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    public void ValidPublicUrlsAreAccepted(string configured, string expected)
    {
        using TestDataDirectory root = new();
        IConfiguration configuration = CreateConfiguration(configured);

        CreatorToolkitOptions options =
            CreatorToolkitOptionsValidator.GetValidated(configuration, root.Path);

        Assert.Equal(expected, options.PublicUrl?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("/relative")]
    [InlineData("https://user@example.invalid")]
    [InlineData("https://example.invalid/?token=value")]
    [InlineData("https://example.invalid/#fragment")]
    public void UnsafePublicUrlsAreRejected(string configured)
    {
        using TestDataDirectory root = new();
        IConfiguration configuration = CreateConfiguration(configured);

        Assert.Throws<InvalidOperationException>(
            () => CreatorToolkitOptionsValidator.GetValidated(configuration, root.Path));
    }

    [Fact]
    public void InvalidConfigurationDoesNotEchoPathsOrUrlSecrets()
    {
        using TestDataDirectory root = new();
        const string pathMarker = "private-path-marker-862c";
        IConfiguration invalidPath = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataDirectory"] = $"{pathMarker}\0invalid",
                })
            .Build();

        InvalidOperationException pathException = Assert.Throws<InvalidOperationException>(
            () => CreatorToolkitOptionsValidator.GetValidated(invalidPath, root.Path));
        Assert.DoesNotContain(pathMarker, pathException.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, pathException.ToString(), StringComparison.Ordinal);

        const string urlSecret = "url-secret-marker-901d";
        IConfiguration invalidUrl = CreateConfiguration(
            $"https://example.invalid/?token={urlSecret}");
        InvalidOperationException urlException = Assert.Throws<InvalidOperationException>(
            () => CreatorToolkitOptionsValidator.GetValidated(invalidUrl, root.Path));
        Assert.DoesNotContain(urlSecret, urlException.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedForwardingSourcesMustBeExplicitValidAddressesOrNetworks()
    {
        using TestDataDirectory root = new();
        IConfiguration valid = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TrustedProxies:0"] = "192.0.2.10",
                    ["TrustedNetworks:0"] = "198.51.100.0/24",
                })
            .Build();

        CreatorToolkitOptions options =
            CreatorToolkitOptionsValidator.GetValidated(valid, root.Path);

        Assert.Equal("192.0.2.10", Assert.Single(options.TrustedProxies).ToString());
        Assert.Equal("198.51.100.0/24", Assert.Single(options.TrustedNetworks).ToString());

        const string marker = "proxy-secret-marker";
        IConfiguration invalid = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TrustedProxies:0"] = marker,
                })
            .Build();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreatorToolkitOptionsValidator.GetValidated(invalid, root.Path));
        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedForwardingSourcesAcceptPortableCommaSeparatedValues()
    {
        using TestDataDirectory root = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TrustedProxies"] =
                        " 192.0.2.10, ,198.51.100.20,192.0.2.10 ",
                    ["TrustedNetworks"] =
                        " 192.0.2.0/24,,198.51.100.0/24,192.0.2.0/24 ",
                })
            .Build();

        CreatorToolkitOptions options =
            CreatorToolkitOptionsValidator.GetValidated(configuration, root.Path);

        Assert.Equal(
            ["192.0.2.10", "198.51.100.20"],
            options.TrustedProxies.Select(address => address.ToString()));
        Assert.Equal(
            ["192.0.2.0/24", "198.51.100.0/24"],
            options.TrustedNetworks.Select(network => network.ToString()));
    }

    [Fact]
    public void TrustedForwardingSourcesTrimIgnoreEmptyAndDeduplicateEntries()
    {
        using TestDataDirectory root = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TrustedProxies:0"] = " 192.0.2.10 ",
                    ["TrustedProxies:1"] = "",
                    ["TrustedProxies:2"] = "192.0.2.10",
                    ["TrustedNetworks:0"] = " 198.51.100.0/24 ",
                    ["TrustedNetworks:1"] = "   ",
                    ["TrustedNetworks:2"] = "198.51.100.0/24",
                })
            .Build();

        CreatorToolkitOptions options =
            CreatorToolkitOptionsValidator.GetValidated(configuration, root.Path);

        Assert.Equal("192.0.2.10", Assert.Single(options.TrustedProxies).ToString());
        Assert.Equal("198.51.100.0/24", Assert.Single(options.TrustedNetworks).ToString());
    }

    private static IConfiguration CreateConfiguration(string publicUrl)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["PublicUrl"] = publicUrl,
                })
            .Build();
    }
}
