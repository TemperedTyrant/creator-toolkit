using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class CreatorToolkitWebFactory(
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    private readonly TestDataDirectory _data = new();

    internal string DataDirectory => _data.Path;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataDirectory", _data.Path);
        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DataDirectory"] = _data.Path,
                    });
            });
        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _data.Dispose();
        }
    }
}
