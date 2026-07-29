using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal static class TestServices
{
    internal static ServiceProvider Create(
        string dataDirectory,
        ICollection<string>? logMessages = null,
        TimeProvider? timeProvider = null,
        IDiagnosticReferenceGenerator? diagnosticReferenceGenerator = null,
        Action<IServiceCollection>? configureServices = null)
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
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddCreatorToolkitInfrastructure(dataDirectory);
        if (diagnosticReferenceGenerator is not null)
        {
            services.RemoveAll<IDiagnosticReferenceGenerator>();
            services.AddSingleton(diagnosticReferenceGenerator);
        }
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    internal static async Task InitializeAsync(ServiceProvider provider)
    {
        await provider.GetRequiredService<PersistenceInitializer>().InitializeAsync();
    }
}
