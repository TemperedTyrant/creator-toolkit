using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Announcements;

public sealed class AnnouncementPersistenceTests
{
    private static readonly Guid ActorId = new("a4262e17-c783-4c18-a6ae-4372289a67eb");
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DraftRoundTripsAcrossProviderRestartWithControlledTimeAndAudit()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(InitialTime);
        Guid id = Guid.NewGuid();

        await using (ServiceProvider first =
                     TestServices.Create(data.Path, timeProvider: timeProvider))
        {
            await TestServices.InitializeAsync(first);
            await using AsyncServiceScope scope = first.CreateAsyncScope();
            AnnouncementOperationResult created = await scope.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .CreateAsync(id, "  Restart draft  ", "Line one\n\nLine two", ActorId);
            Assert.Equal(AnnouncementOperationStatus.Succeeded, created.Status);
            Assert.Equal(1, created.Revision);
        }

        await using ServiceProvider second =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(second);
        await using AsyncServiceScope verification = second.CreateAsyncScope();
        AnnouncementDetails item = Assert.IsType<AnnouncementDetails>(
            await verification.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .GetAsync(id));
        Assert.Equal("Restart draft", item.Title);
        Assert.Equal("Line one\n\nLine two", item.Body);
        Assert.Equal(AnnouncementStatus.Draft, item.Status);
        Assert.Equal(InitialTime, item.CreatedAtUtc);
        Assert.Equal(InitialTime, item.UpdatedAtUtc);
        Assert.Equal(1, item.Revision);
        Assert.Equal(
            "announcement.created",
            (await verification.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .AuditRecords
                .SingleAsync()).EventCode);
    }

    [Fact]
    public async Task ListUsesBoundedPagingFilteringLiteralSearchAndUpdatedDescendingOrder()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(InitialTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);
        List<Guid> ids = [];

        for (int index = 0; index < 30; index++)
        {
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            Guid id = Guid.NewGuid();
            ids.Add(id);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await scope.ServiceProvider
                    .GetRequiredService<IAnnouncementService>()
                    .CreateAsync(
                        id,
                        index == 4 ? "Literal 100% update" : $"Draft {index:D2}",
                        index == 8 ? "Body with search needle" : "Standard body",
                        ActorId)).Status);
        }

        await using (AsyncServiceScope archiveScope = provider.CreateAsyncScope())
        {
            IAnnouncementService service = archiveScope.ServiceProvider
                .GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.ArchiveAsync(ids[0], 1, ActorId)).Status);
        }

        await using AsyncServiceScope queryScope = provider.CreateAsyncScope();
        IAnnouncementService query = queryScope.ServiceProvider
            .GetRequiredService<IAnnouncementService>();
        AnnouncementPage firstPage = await query.ListAsync(
            new AnnouncementListRequest(null, AnnouncementStatusFilter.Draft, -20));
        AnnouncementPage secondPage = await query.ListAsync(
            new AnnouncementListRequest(
                null,
                AnnouncementStatusFilter.Draft,
                2));
        AnnouncementPage archived = await query.ListAsync(
            new AnnouncementListRequest(
                null,
                AnnouncementStatusFilter.Archived,
                1));
        AnnouncementPage titleSearch = await query.ListAsync(
            new AnnouncementListRequest(
                "100%",
                AnnouncementStatusFilter.All,
                1));
        AnnouncementPage bodySearch = await query.ListAsync(
            new AnnouncementListRequest(
                "search needle",
                AnnouncementStatusFilter.All,
                1));

        Assert.Equal(1, firstPage.Page);
        Assert.Equal(25, firstPage.Items.Count);
        Assert.Equal(29, firstPage.TotalItems);
        Assert.Equal(2, firstPage.TotalPages);
        Assert.Equal(4, secondPage.Items.Count);
        Assert.Equal(ids[29], firstPage.Items[0].Id);
        Assert.Single(archived.Items);
        Assert.Equal(ids[0], archived.Items[0].Id);
        Assert.Single(titleSearch.Items);
        Assert.Equal(ids[4], titleSearch.Items[0].Id);
        Assert.Single(bodySearch.Items);
        Assert.Equal(ids[8], bodySearch.Items[0].Id);
    }

    [Fact]
    public async Task TwoLoadedRevisionsAllowOnlyFirstWriterToCommit()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> factory = provider
            .GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        Guid id = Guid.NewGuid();

        await using (AsyncServiceScope createScope = provider.CreateAsyncScope())
        {
            await createScope.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .CreateAsync(id, "Original", "Original body", ActorId);
        }

        await using CreatorToolkitDbContext first = await factory.CreateDbContextAsync();
        await using CreatorToolkitDbContext second = await factory.CreateDbContextAsync();
        Announcement firstCopy = await first.Announcements.SingleAsync(value => value.Id == id);
        Announcement secondCopy = await second.Announcements.SingleAsync(value => value.Id == id);
        Assert.Equal(1, firstCopy.Revision);
        Assert.Equal(1, secondCopy.Revision);
        Assert.Equal(
            AnnouncementDomainStatus.Succeeded,
            firstCopy.Update(
                "First writer",
                "First body",
                1,
                ActorId,
                InitialTime.AddMinutes(1)).Status);
        Assert.Equal(
            AnnouncementDomainStatus.Succeeded,
            secondCopy.Update(
                "Late writer",
                "Late body",
                1,
                ActorId,
                InitialTime.AddMinutes(2)).Status);

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync());

        await using CreatorToolkitDbContext verification =
            await factory.CreateDbContextAsync();
        Announcement stored = await verification.Announcements
            .AsNoTracking()
            .SingleAsync(value => value.Id == id);
        Assert.Equal("First writer", stored.Title);
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task StaleStateOperationsMakeNoPartialChangeOrAuditEntry()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid id = Guid.NewGuid();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IAnnouncementService service = scope.ServiceProvider
                .GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.CreateAsync(id, "Draft", "Body", ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.UpdateAsync(id, "Current", "Current body", 1, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.StaleRevision,
                (await service.ArchiveAsync(id, 1, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.ArchiveAsync(id, 2, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.StaleRevision,
                (await service.RestoreAsync(id, 2, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.StaleRevision,
                (await service.DeleteAsync(id, 2, ActorId)).Status);
        }

        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Announcement stored = await db.Announcements.AsNoTracking().SingleAsync();
        Assert.Equal(AnnouncementStatus.Archived, stored.Status);
        Assert.Equal("Current", stored.Title);
        Assert.Equal(3, stored.Revision);
        Assert.Equal(
            ["announcement.archived", "announcement.created", "announcement.updated"],
            await db.AuditRecords
                .OrderBy(value => value.EventCode)
                .Select(value => value.EventCode)
                .ToArrayAsync());
    }

    [Fact]
    public async Task FullLifecycleAuditsSafeMetadataAndDeleteRetainsAuditWithoutContent()
    {
        using TestDataDirectory data = new();
        string titleCanary = "announcement-title-canary-c921f42a";
        string bodyCanary = "announcement-body-canary-803669cb";
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid id = Guid.NewGuid();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IAnnouncementService service = scope.ServiceProvider
                .GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.CreateAsync(id, titleCanary, bodyCanary, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.DuplicateSubmission,
                (await service.CreateAsync(id, "Replacement", "Replacement", ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.UpdateAsync(id, "Updated", "Updated body", 1, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.ArchiveAsync(id, 2, ActorId)).Status);
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await service.RestoreAsync(id, 3, ActorId)).Status);
            AnnouncementOperationResult deleted = await service.DeleteAsync(id, 4, ActorId);
            Assert.Equal(AnnouncementOperationStatus.Succeeded, deleted.Status);
            Assert.Equal(5, deleted.Revision);
        }

        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.Announcements.ToListAsync());
        var audits = await db.AuditRecords
            .Select(
                value => new
                {
                    value.EventCode,
                    value.ActorUserId,
                    value.TargetUserId,
                    value.ReasonCode,
                    value.DiagnosticReference,
                    value.Outcome,
                })
            .ToArrayAsync();
        Assert.Equal(5, audits.Length);
        Assert.Equal(
            [
                "announcement.archived",
                "announcement.created",
                "announcement.deleted",
                "announcement.restored",
                "announcement.updated",
            ],
            audits.Select(value => value.EventCode).Order());
        Assert.All(audits, value => Assert.Equal(ActorId, value.ActorUserId));
        Assert.All(audits, value => Assert.Null(value.TargetUserId));
        Assert.All(audits, value => Assert.Null(value.ReasonCode));
        Assert.All(audits, value => Assert.Null(value.DiagnosticReference));
        Assert.All(audits, value => Assert.Equal("succeeded", value.Outcome));

        string serializedAudit = string.Join(
            '|',
            audits.Select(
                value => $"{value.EventCode}:{value.ActorUserId}:{value.Outcome}"));
        Assert.DoesNotContain(titleCanary, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(bodyCanary, serializedAudit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedMutationRollsBackItsSuccessfulAuditRecord()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid id = Guid.NewGuid();

        await using (AsyncServiceScope createScope = provider.CreateAsyncScope())
        {
            await createScope.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .CreateAsync(id, "Original", "Original body", ActorId);
        }

        DataDirectoryLayout layout = provider
            .GetRequiredService<DataDirectoryLayoutProvider>()
            .Layout;
        await using (SqliteConnection connection = new($"Data Source={layout.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TRIGGER RejectAnnouncementUpdate
                BEFORE UPDATE ON Announcements
                BEGIN
                    SELECT RAISE(ABORT, 'controlled update rejection');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (AsyncServiceScope updateScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => updateScope.ServiceProvider
                    .GetRequiredService<IAnnouncementService>()
                    .UpdateAsync(id, "Rejected", "Rejected body", 1, ActorId));
        }

        await using AsyncServiceScope verification = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Announcement stored = await db.Announcements.AsNoTracking().SingleAsync();
        Assert.Equal("Original", stored.Title);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(1, await db.AuditRecords.CountAsync());
        Assert.Equal(
            0,
            await db.AuditRecords.CountAsync(
                value => value.EventCode == "announcement.updated"));
    }
}
