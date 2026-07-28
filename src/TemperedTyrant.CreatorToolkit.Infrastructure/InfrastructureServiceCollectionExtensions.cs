using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddCreatorToolkitInfrastructure(
        this IServiceCollection services,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);

        DataDirectoryLayout layout = DataDirectoryLayout.Prepare(dataDirectory);
        DataDirectoryLayoutProvider layoutProvider = new(layout);
        SqliteConnectionInterceptor connectionInterceptor = new();
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();

        services.AddSingleton(layoutProvider);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(connectionInterceptor);
        // These framework categories can render configured repository or data-source paths.
        // PersistenceInitializer emits the path-safe startup failure event instead.
        services.AddLogging(
            logging =>
            {
                logging.AddFilter(
                    "Microsoft.AspNetCore.DataProtection",
                    LogLevel.None);
                logging.AddFilter(
                    "Microsoft.EntityFrameworkCore.Database.Connection",
                    LogLevel.None);
            });
        services.AddDbContextFactory<CreatorToolkitDbContext>(
            options => options
                .UseSqlite(connectionString)
                .AddInterceptors(connectionInterceptor));
        services
            .AddDataProtection()
            .SetApplicationName("TemperedTyrant.CreatorToolkit")
            .PersistKeysToFileSystem(new DirectoryInfo(layout.KeyRingPath));
        services.AddSingleton<ApplicationHostLock>();
        services.AddSingleton<MigrationLock>();
        services.AddSingleton<SecurityOperationCoordinator>();
        services.AddSingleton<MigrationCoordinator>();
        services.AddSingleton<PersistenceInitializer>();
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();

        return services;
    }
}
