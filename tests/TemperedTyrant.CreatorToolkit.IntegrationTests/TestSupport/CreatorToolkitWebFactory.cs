using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class CreatorToolkitWebFactory : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly TestDataDirectory? _ownedData;
    private readonly string _dataDirectory;

    internal CreatorToolkitWebFactory(
        Action<IServiceCollection>? configureServices = null,
        string? dataDirectory = null)
    {
        _configureServices = configureServices;
        _ownedData = dataDirectory is null ? new TestDataDirectory() : null;
        _dataDirectory = dataDirectory ?? _ownedData!.Path;
    }

    internal string DataDirectory => _dataDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataDirectory", _dataDirectory);
        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DataDirectory"] = _dataDirectory,
                    });
            });
        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _ownedData?.Dispose();
        }
    }
}
