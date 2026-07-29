using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class AuditPersistenceTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuditWriterStagesRecordUntilCallerSavesAndCommits()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(FixedTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext context =
            scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        IAuditWriter writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();

        await using var transaction = await context.Database.BeginTransactionAsync();
        await writer.WriteAsync(
            new AuditEvent(
                AuditEventCode.ProtectedOperation,
                AuditOutcome.Succeeded));

        await using (CreatorToolkitDbContext beforeSave =
                     await contextFactory.CreateDbContextAsync())
        {
            Assert.Empty(await beforeSave.AuditRecords.ToListAsync());
        }

        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        AuditRecord record = await verification.AuditRecords.SingleAsync();
        Assert.Equal(FixedTime, record.OccurredAtUtc);
        Assert.Equal("security.protected-operation", record.EventCode);
        Assert.Equal("succeeded", record.Outcome);
    }

    [Fact]
    public async Task AuditRecordRollsBackWithCallersTransaction()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext context =
                scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            IAuditWriter writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            await writer.WriteAsync(
                new AuditEvent(
                    AuditEventCode.ProtectedOperation,
                    AuditOutcome.Failed,
                    ReasonCode: AuditReasonCode.UnexpectedFailure));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Empty(await verification.AuditRecords.ToListAsync());
    }

    [Fact]
    public async Task AuditTimestampIsNormalizedToUtcFromInjectedTimeProvider()
    {
        using TestDataDirectory data = new();
        DateTimeOffset configuredTime =
            new(2026, 7, 28, 17, 30, 0, TimeSpan.FromHours(5));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: new ManualTimeProvider(configuredTime));
        await TestServices.InitializeAsync(provider);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IAuditWriter writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            CreatorToolkitDbContext context =
                scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            await writer.WriteAsync(
                new AuditEvent(
                    AuditEventCode.ProtectedOperation,
                    AuditOutcome.Succeeded));
            await context.SaveChangesAsync();
        }

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        AuditRecord record = await verification.AuditRecords.SingleAsync();
        Assert.Equal(configuredTime.ToUniversalTime(), record.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, record.OccurredAtUtc.Offset);
    }

    [Fact]
    public async Task RequiredAuditFailurePreventsProtectedTransactionFromCommitting()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext context =
                scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            IAuditWriter writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Workspaces (Id, TimeZoneId, CreatedAtUtc, Revision)
                VALUES (1, {"Etc/UTC"}, {FixedTime}, 0);
                """);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.WriteAsync(
                    new AuditEvent(
                        AuditEventCode.ProtectedOperation,
                        AuditOutcome.Succeeded),
                    cancellation.Token));

            await transaction.RollbackAsync();
        }

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Empty(await verification.Workspaces.ToListAsync());
        Assert.Empty(await verification.AuditRecords.ToListAsync());
    }

    [Fact]
    public async Task SupportedOperationsRejectAuditUpdatesAndDeletes()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext context = await contextFactory.CreateDbContextAsync();
        Guid auditId = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AuditRecords
                (Id, OccurredAtUtc, EventCode, Outcome)
            VALUES
                ({auditId}, {FixedTime}, {"test.event"}, {"succeeded"});
            """);

        AuditRecord audit = await context.AuditRecords.SingleAsync();
        context.Entry(audit).Property(nameof(AuditRecord.Outcome)).CurrentValue = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();
        audit = await context.AuditRecords.SingleAsync();
        context.AuditRecords.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }
}
