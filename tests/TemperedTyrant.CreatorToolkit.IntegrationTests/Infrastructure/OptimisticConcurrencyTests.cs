using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Security;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed class OptimisticConcurrencyTests
{
    [Fact]
    public async Task EveryRevisionedSecurityRecordRejectsAStaleMutation()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        Guid userId = Guid.NewGuid();
        Guid capabilityId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (CreatorToolkitDbContext setup = await contextFactory.CreateDbContextAsync())
        {
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AspNetUsers
                    (Id, DisplayName, IsEnabled, CreatedAtUtc, EmailConfirmed,
                     PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES
                    ({userId}, {"Concurrency user"}, 1, {now}, 0, 0, 0, 1, 0);

                INSERT INTO Workspaces (Id, TimeZoneId, CreatedAtUtc, Revision)
                VALUES (1, {"Etc/UTC"}, {now}, 0);

                INSERT INTO Ownerships (WorkspaceId, OwnerUserId, TransferredAtUtc, Revision)
                VALUES (1, {userId}, {now}, 0);

                INSERT INTO SecurityCapabilities
                    (Id, Purpose, TokenHash, ActiveSlot, CreatedAtUtc, ExpiresAtUtc, Revision)
                VALUES
                    ({capabilityId}, {"RecoverOwner"}, {new byte[32]}, NULL,
                     {now}, {now.AddHours(2)}, 0);
                """);
        }

        SecretReference secretReference;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ISecretStore secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            secretReference = await secretStore.CreateAsync("concurrency-test", "secret-value");
        }

        await using (CreatorToolkitDbContext modelContext =
                     await contextFactory.CreateDbContextAsync())
        {
            Type protectedSecretType = GetProtectedSecretType(modelContext);
            Type[] revisionedTypes =
            [
                typeof(InstallationState),
                typeof(Workspace),
                typeof(Ownership),
                typeof(SecurityCapability),
                protectedSecretType,
            ];

            Assert.All(
                revisionedTypes,
                entityType =>
                {
                    var revision = modelContext.Model
                        .FindEntityType(entityType)?
                        .FindProperty("Revision");
                    Assert.NotNull(revision);
                    Assert.True(revision.IsConcurrencyToken);
                });
        }

        await AssertStaleMutationRejectedAsync(
            contextFactory,
            typeof(InstallationState),
            InstallationState.SingletonId,
            nameof(InstallationState.InitializedAtUtc),
            now,
            now.AddMinutes(1));
        await AssertStaleMutationRejectedAsync(
            contextFactory,
            typeof(Workspace),
            Workspace.SingletonId,
            nameof(Workspace.TimeZoneId),
            "America/Toronto",
            "Europe/London");
        await AssertStaleMutationRejectedAsync(
            contextFactory,
            typeof(Ownership),
            Workspace.SingletonId,
            nameof(Ownership.TransferredAtUtc),
            now.AddMinutes(2),
            now.AddMinutes(3));
        await AssertStaleMutationRejectedAsync(
            contextFactory,
            typeof(SecurityCapability),
            capabilityId,
            nameof(SecurityCapability.ExpiresAtUtc),
            now.AddHours(3),
            now.AddHours(4));

        await using (CreatorToolkitDbContext typeContext =
                     await contextFactory.CreateDbContextAsync())
        {
            await AssertStaleMutationRejectedAsync(
                contextFactory,
                GetProtectedSecretType(typeContext),
                secretReference.Id,
                "UpdatedAtUtc",
                now.AddMinutes(4),
                now.AddMinutes(5));
        }
    }

    [Fact]
    public async Task IdentityUserStoreRejectsAStaleSecurityRecordUpdate()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        Guid userId = Guid.NewGuid();

        await using (CreatorToolkitDbContext setup = await contextFactory.CreateDbContextAsync())
        {
            setup.Users.Add(
                new ApplicationUser
                {
                    Id = userId,
                    UserName = "concurrency-user",
                    NormalizedUserName = "CONCURRENCY-USER",
                    DisplayName = "Initial",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                });
            await setup.SaveChangesAsync();
        }

        await using CreatorToolkitDbContext first = await contextFactory.CreateDbContextAsync();
        await using CreatorToolkitDbContext second = await contextFactory.CreateDbContextAsync();
        using UserStore<ApplicationUser, IdentityRole<Guid>, CreatorToolkitDbContext, Guid>
            firstStore = new(first);
        using UserStore<ApplicationUser, IdentityRole<Guid>, CreatorToolkitDbContext, Guid>
            secondStore = new(second);
        ApplicationUser firstUser = await first.Users.SingleAsync(user => user.Id == userId);
        ApplicationUser secondUser = await second.Users.SingleAsync(user => user.Id == userId);
        firstUser.DisplayName = "First";
        secondUser.DisplayName = "Second";

        IdentityResult firstResult = await firstStore.UpdateAsync(firstUser, CancellationToken.None);
        IdentityResult secondResult = await secondStore.UpdateAsync(secondUser, CancellationToken.None);

        Assert.True(firstResult.Succeeded);
        Assert.False(secondResult.Succeeded);
        Assert.Contains(secondResult.Errors, error => error.Code == "ConcurrencyFailure");
    }

    private static async Task AssertStaleMutationRejectedAsync(
        IDbContextFactory<CreatorToolkitDbContext> contextFactory,
        Type entityType,
        object key,
        string propertyName,
        object firstValue,
        object secondValue)
    {
        await using CreatorToolkitDbContext first = await contextFactory.CreateDbContextAsync();
        await using CreatorToolkitDbContext second = await contextFactory.CreateDbContextAsync();
        object firstEntity = await first.FindAsync(
                entityType,
                [key],
                CancellationToken.None)
            ?? throw new InvalidOperationException("The first concurrency record was not found.");
        object secondEntity = await second.FindAsync(
                entityType,
                [key],
                CancellationToken.None)
            ?? throw new InvalidOperationException("The second concurrency record was not found.");
        first.Entry(firstEntity).Property(propertyName).CurrentValue = firstValue;
        second.Entry(secondEntity).Property(propertyName).CurrentValue = secondValue;

        await first.SaveChangesAsync();
        Assert.Equal(
            1L,
            (long)(first.Entry(firstEntity).Property("Revision").CurrentValue ?? -1L));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static Type GetProtectedSecretType(CreatorToolkitDbContext context)
    {
        return context.Model
            .GetEntityTypes()
            .Single(entity => entity.ClrType.Name == "ProtectedSecretRecord")
            .ClrType;
    }
}
