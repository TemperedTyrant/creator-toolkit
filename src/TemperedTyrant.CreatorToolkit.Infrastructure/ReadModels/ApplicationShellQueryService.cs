using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.ReadModels;

public sealed class ApplicationShellQueryService(CreatorToolkitDbContext dbContext)
{
    private const int MaximumRecentDiagnostics = 25;

    public async Task<DashboardState> GetDashboardAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        DashboardUser user = await dbContext.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new DashboardUser(candidate.UserName!, candidate.DisplayName))
            .SingleAsync(cancellationToken);
        string role = await (
                from userRole in dbContext.UserRoles
                join candidateRole in dbContext.Roles on userRole.RoleId equals candidateRole.Id
                where userRole.UserId == userId
                select candidateRole.Name!)
            .SingleAsync(cancellationToken);
        bool initialized = await dbContext.InstallationStates
            .AsNoTracking()
            .AnyAsync(state => state.InitializedAtUtc != null, cancellationToken);

        return new DashboardState(user.UserName, user.DisplayName, role, initialized);
    }

    public async Task<DebugPersistenceState> GetDebugStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return DebugPersistenceState.Inaccessible;
            }

            bool migrationsCurrent = !(await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .Any();
            bool initialized = await dbContext.InstallationStates
                .AsNoTracking()
                .AnyAsync(state => state.InitializedAtUtc != null, cancellationToken);
            DebugDiagnostic[] persistedDiagnostics = await dbContext.DiagnosticRecords
                .AsNoTracking()
                .Select(
                    record => new DebugDiagnostic(
                        record.Reference,
                        record.OccurredAtUtc,
                        record.ErrorCode))
                .ToArrayAsync(cancellationToken);
            DebugDiagnostic[] diagnostics = persistedDiagnostics
                .OrderByDescending(record => record.OccurredAtUtc)
                .ThenByDescending(record => record.Reference, StringComparer.Ordinal)
                .Take(MaximumRecentDiagnostics)
                .ToArray();

            return new DebugPersistenceState(
                DatabaseAccessible: true,
                MigrationsCurrent: migrationsCurrent,
                InstallationInitialized: initialized,
                RecentDiagnostics: diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DebugPersistenceState.Inaccessible;
        }
    }

    private sealed record DashboardUser(string UserName, string DisplayName);
}

public sealed record DashboardState(
    string UserName,
    string DisplayName,
    string Role,
    bool InstallationInitialized);

public sealed record DebugPersistenceState(
    bool DatabaseAccessible,
    bool MigrationsCurrent,
    bool InstallationInitialized,
    IReadOnlyList<DebugDiagnostic> RecentDiagnostics)
{
    public static DebugPersistenceState Inaccessible { get; } =
        new(false, false, false, []);
}

public sealed record DebugDiagnostic(
    string Reference,
    DateTimeOffset OccurredAtUtc,
    string Code);
