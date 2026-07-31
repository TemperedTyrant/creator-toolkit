using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed partial class PersistenceInitializer(
    MigrationCoordinator migrationCoordinator,
    IDbContextFactory<CreatorToolkitDbContext> contextFactory,
    IDataProtectionValidator dataProtectionValidator,
    DataDirectoryLayoutProvider layoutProvider,
    PersistenceInitializationState initializationState,
    ILogger<PersistenceInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        initializationState.MarkRunning();
        try
        {
            await InitializeCoreAsync(cancellationToken);
            initializationState.MarkSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            initializationState.MarkFailed();
            throw;
        }
        catch (Exception exception)
        {
            initializationState.MarkFailed();
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

        if (context.Database.HasPendingModelChanges())
        {
            throw new InvalidOperationException("The persistence model is not current.");
        }

        if (!dataProtectionValidator.IsUsable())
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
