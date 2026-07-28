using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(connectionInterceptor);
        services.AddLogging();
        services.Configure<LoggerFilterOptions>(
            options =>
            {
                options.Rules.Clear();
                options.MinLevel = LogLevel.Trace;
                options.Rules.Add(
                    new LoggerFilterRule(
                        providerName: null,
                        categoryName: null,
                        logLevel: LogLevel.Trace,
                        filter: (_, category, level) =>
                            category?.StartsWith(
                                "TemperedTyrant.CreatorToolkit.",
                                StringComparison.Ordinal) == true
                            && level >= LogLevel.Information));
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
        services.AddScoped<IAuditWriter, TransactionalAuditWriter>();
        services.AddSingleton<
            IDiagnosticReferenceGenerator,
            CryptographicDiagnosticReferenceGenerator>();
        services.AddScoped<DiagnosticPersistence>();
        services.AddSingleton<IDiagnosticRecorder, BestEffortDiagnosticRecorder>();
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();

        return services;
    }
}
