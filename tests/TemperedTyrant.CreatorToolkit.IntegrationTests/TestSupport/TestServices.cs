using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal static class TestServices
{
    internal static ServiceProvider Create(
        string dataDirectory,
        ICollection<string>? logMessages = null)
    {
        ServiceCollection services = new();
        services.AddLogging(
            logging =>
            {
                if (logMessages is not null)
                {
                    logging.AddProvider(new TestLoggerProvider(logMessages));
                }
            });
        services.AddCreatorToolkitInfrastructure(dataDirectory);
        return services.BuildServiceProvider(validateScopes: true);
    }

    internal static async Task InitializeAsync(ServiceProvider provider)
    {
        await provider.GetRequiredService<PersistenceInitializer>().InitializeAsync();
    }
}
