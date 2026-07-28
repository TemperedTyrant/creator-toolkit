using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class AuditPersistenceTests
{
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
                ({auditId}, {DateTimeOffset.UtcNow}, {"test.event"}, {"succeeded"});
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
