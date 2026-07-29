using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed partial class PersistenceInitializer(
    MigrationCoordinator migrationCoordinator,
    IDbContextFactory<CreatorToolkitDbContext> contextFactory,
    IDataProtectionProvider dataProtectionProvider,
    DataDirectoryLayoutProvider layoutProvider,
    ILogger<PersistenceInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Critical))
            {
                LogInitializationFailure(logger, exception.HResult);
            }

            throw new InvalidOperationException(
                "Protected local persistence initialization failed.");
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await migrationCoordinator.MigrateForWebHostAsync(cancellationToken);

        await using CreatorToolkitDbContext context =
            await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);

        IDataProtector protector = dataProtectionProvider.CreateProtector(
            "TemperedTyrant.CreatorToolkit.StartupValidation.v1");
        const string canary = "data-protection-startup-canary";
        string protectedCanary = protector.Protect(canary);

        if (!string.Equals(protector.Unprotect(protectedCanary), canary, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Data Protection key-ring validation failed.");
        }

        SetRestrictiveKeyPermissions(layoutProvider.Layout.KeyRingPath);
    }

    private static void SetRestrictiveKeyPermissions(string keyRingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string keyFile in Directory.EnumerateFiles(keyRingPath))
        {
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Critical,
        Message = "Protected local persistence initialization failed with error code {ErrorCode}.")]
    private static partial void LogInitializationFailure(ILogger logger, int errorCode);
}
