using Microsoft.AspNetCore.DataProtection;
using TemperedTyrant.CreatorToolkit.Infrastructure.ReadModels;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.Hosting;

namespace TemperedTyrant.CreatorToolkit.Web.Diagnostics;

public sealed class DebugStatusService(
    ApplicationShellQueryService queryService,
    IDataProtectionProvider dataProtectionProvider,
    CreatorToolkitOptions options,
    ApplicationLifecycleCoordinator lifecycleCoordinator)
{
    public async Task<DebugPageStatus> GetAsync(
        CancellationToken cancellationToken = default)
    {
        DebugPersistenceState persistence = await queryService.GetDebugStateAsync(
            cancellationToken);

        return new DebugPageStatus(
            ApplicationVersion: typeof(Program).Assembly.GetName().Version?.ToString(3)
                ?? "unknown",
            LifecycleState: lifecycleCoordinator.GetStatus().State,
            InstallationInitialized: persistence.InstallationInitialized,
            MigrationsCurrent: persistence.MigrationsCurrent,
            DatabaseAccessible: persistence.DatabaseAccessible,
            KeyRingAccessible: IsKeyRingAccessible(),
            PublicUrlConfigured: options.PublicUrl is not null,
            TrustedProxyCount: options.TrustedProxies.Count,
            TrustedNetworkCount: options.TrustedNetworks.Count,
            RecentDiagnostics: persistence.RecentDiagnostics
                .Select(
                    diagnostic => new DebugDiagnosticItem(
                        diagnostic.Reference,
                        diagnostic.OccurredAtUtc,
                        diagnostic.Code))
                .ToArray());
    }

    private bool IsKeyRingAccessible()
    {
        try
        {
            IDataProtector protector = dataProtectionProvider.CreateProtector(
                "TemperedTyrant.CreatorToolkit.Debug.KeyRingProbe.v1");
            const string probe = "key-ring-accessibility-probe";
            return string.Equals(
                protector.Unprotect(protector.Protect(probe)),
                probe,
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record DebugPageStatus(
    string ApplicationVersion,
    ApplicationLifecycleState LifecycleState,
    bool InstallationInitialized,
    bool MigrationsCurrent,
    bool DatabaseAccessible,
    bool KeyRingAccessible,
    bool PublicUrlConfigured,
    int TrustedProxyCount,
    int TrustedNetworkCount,
    IReadOnlyList<DebugDiagnosticItem> RecentDiagnostics);

public sealed record DebugDiagnosticItem(
    string Reference,
    DateTimeOffset OccurredAtUtc,
    string Code);
