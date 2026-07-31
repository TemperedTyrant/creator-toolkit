using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Security;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Health;

internal sealed class InfrastructureReadinessProbe(
    PersistenceInitializationState initializationState,
    IDbContextFactory<CreatorToolkitDbContext> contextFactory,
    IDataProtectionValidator dataProtectionValidator,
    TimeProvider timeProvider) : IInfrastructureReadinessProbe
{
    private static readonly TimeSpan DatabaseCommandTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DatabaseOperationTimeout =
        TimeSpan.FromMilliseconds(1500);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (initializationState.GetStatus() != PersistenceInitializationStatus.Succeeded)
        {
            return false;
        }

        try
        {
            using CancellationTokenSource operationTimeoutSource =
                new(DatabaseOperationTimeout, timeProvider);
            using CancellationTokenSource operationSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    operationTimeoutSource.Token);
            CancellationToken operationToken = operationSource.Token;

            await using CreatorToolkitDbContext context =
                await contextFactory.CreateDbContextAsync(operationToken);
            context.Database.SetCommandTimeout(DatabaseCommandTimeout);

            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(operationToken);
            await using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1;";
                command.CommandTimeout = (int)DatabaseCommandTimeout.TotalSeconds;
                object? result = await command.ExecuteScalarAsync(operationToken);
                if (result is not long value || value != 1)
                {
                    return false;
                }
            }

            bool migrationsCurrent = !(await context.Database
                    .GetPendingMigrationsAsync(operationToken))
                .Any();

            return migrationsCurrent
                && await dataProtectionValidator.IsUsableAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
