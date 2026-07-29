using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed class MigrationCoordinator(
    MigrationLock migrationLock,
    IDbContextFactory<CreatorToolkitDbContext> contextFactory,
    ApplicationHostLock applicationHostLock)
{
    public Task MigrateForWebHostAsync(CancellationToken cancellationToken = default)
    {
        return migrationLock.ExecuteAsync(
            async operationToken =>
            {
                await using CreatorToolkitDbContext context =
                    await contextFactory.CreateDbContextAsync(operationToken);
                await context.Database.MigrateAsync(operationToken);
            },
            cancellationToken);
    }

    public Task EnsureCurrentForAdministrativeCommandAsync(
        CancellationToken cancellationToken = default)
    {
        return migrationLock.ExecuteAsync(
            async operationToken =>
            {
                await using CreatorToolkitDbContext context =
                    await contextFactory.CreateDbContextAsync(operationToken);
                bool hasPendingMigrations = (await context.Database
                    .GetPendingMigrationsAsync(operationToken))
                    .Any();

                if (!hasPendingMigrations)
                {
                    return;
                }

                if (applicationHostLock.IsHeld())
                {
                    throw new InvalidOperationException(
                        "Database migrations are pending while the application host is running.");
                }

                await context.Database.MigrateAsync(operationToken);
            },
            cancellationToken);
    }
}
