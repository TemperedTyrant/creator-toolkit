using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Health;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;
using TemperedTyrant.CreatorToolkit.Infrastructure.ReadModels;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;

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
            .AddIdentityCore<ApplicationUser>(
                options =>
                {
                    options.User.RequireUniqueEmail = false;
                    options.SignIn.RequireConfirmedAccount = false;
                    options.SignIn.RequireConfirmedEmail = false;
                    options.SignIn.RequireConfirmedPhoneNumber = false;
                    options.Password.RequiredLength =
                        CreatorToolkitPasswordValidator.MinimumScalarCount;
                    options.Password.RequiredUniqueChars = 1;
                    options.Password.RequireUppercase = false;
                    options.Password.RequireLowercase = false;
                    options.Password.RequireDigit = false;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Lockout.AllowedForNewUsers = true;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CreatorToolkitDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.RemoveAll<IPasswordHasher<ApplicationUser>>();
        services.AddScoped<PasswordHasher<ApplicationUser>>();
        services.AddScoped<IPasswordHasher<ApplicationUser>, NfcPasswordHasher>();
        services.RemoveAll<IPasswordValidator<ApplicationUser>>();
        services.AddScoped<CreatorToolkitPasswordValidator>();
        services.AddScoped<IPasswordValidator<ApplicationUser>>(
            provider => provider.GetRequiredService<CreatorToolkitPasswordValidator>());
        services
            .AddDataProtection()
            .SetApplicationName("TemperedTyrant.CreatorToolkit")
            .PersistKeysToFileSystem(new DirectoryInfo(layout.KeyRingPath));
        services.AddSingleton<ApplicationHostLock>();
        services.AddSingleton<MigrationLock>();
        services.AddSingleton<SecurityOperationCoordinator>();
        services.AddSingleton<MigrationCoordinator>();
        services.AddSingleton<PersistenceInitializationState>();
        services.AddSingleton<IDataProtectionValidator, DataProtectionValidator>();
        services.AddSingleton<PersistenceInitializer>();
        services.AddSingleton<IInfrastructureReadinessProbe, InfrastructureReadinessProbe>();
        services.AddScoped<IAuditWriter, TransactionalAuditWriter>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddSingleton<AnnouncementMediaProtector>();
        services.AddHttpClient<DiscordHttpApi>(
                client =>
                {
                    client.BaseAddress = DiscordHttpApi.ApiBaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "DiscordBot (https://github.com/TemperedTyrant/creator-toolkit, 0.1)");
                })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(
                () => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    MaxConnectionsPerServer = 8,
                    MaxResponseHeadersLength = 32,
                });
        services.AddScoped<IDiscordApi>(
            provider => provider.GetRequiredService<DiscordHttpApi>());
        services.AddScoped<IDiscordConfigurationService, DiscordConfigurationService>();
        services.AddScoped<IDiscordPublishingService, DiscordPublishingService>();
        services.AddScoped<IPublicationHistoryService, PublicationHistoryService>();
        services.AddScoped<PublicationProcessor>();
        services.AddSingleton<PublicationPayloadProtector>();
        services.AddSingleton(PublicationWorkerOptions.Default);
        services.AddSingleton<
            IDiagnosticReferenceGenerator,
            CryptographicDiagnosticReferenceGenerator>();
        services.AddScoped<DiagnosticPersistence>();
        services.AddSingleton<IDiagnosticRecorder, BestEffortDiagnosticRecorder>();
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();
        services.AddScoped<IProtectedSecretValueResolver, ProtectedSecretValueResolver>();
        services.AddScoped<BootstrapCapabilityIssuer>();
        services.AddScoped<InitialOwnerSetupService>();
        services.AddScoped<UserLifecycleService>();
        services.AddScoped<AccountActivationService>();
        services.AddScoped<OwnershipTransferService>();
        services.AddScoped<OwnerRecoveryIssuer>();
        services.AddScoped<OwnerRecoveryService>();
        services.AddScoped<ApplicationShellQueryService>();

        return services;
    }
}
