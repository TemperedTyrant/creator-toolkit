using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Identity;

public sealed class UserLifecycleTests
{
    private const string OwnerPassword = "mild river orbit velvet canyon";
    private const string UserPassword = "silver meadow lantern compass";

    [Fact]
    public async Task AuthorizationMatrixIsEnforcedByTheApplicationService()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser admin = await CreateAndActivateAsync(
            provider,
            ownerId,
            "admin-local",
            SystemRoles.Admin);
        CreatedUser editor = await CreateAndActivateAsync(
            provider,
            ownerId,
            "editor-local",
            SystemRoles.Editor);
        CreatedUser viewer = await CreateAndActivateAsync(
            provider,
            ownerId,
            "viewer-local",
            SystemRoles.Viewer);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        UserLifecycleService service = scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>();

        Assert.Equal(
            UserLifecycleStatus.Succeeded,
            (await service.CreatePendingAsync(
                ownerId,
                "owner-created-admin",
                null,
                SystemRoles.Admin)).Status);
        Assert.Equal(
            UserLifecycleStatus.Succeeded,
            (await service.CreatePendingAsync(
                admin.Id,
                "admin-created-editor",
                null,
                SystemRoles.Editor)).Status);
        Assert.Equal(
            UserLifecycleStatus.Succeeded,
            (await service.CreatePendingAsync(
                admin.Id,
                "admin-created-viewer",
                null,
                SystemRoles.Viewer)).Status);
        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.CreatePendingAsync(
                admin.Id,
                "admin-forged-admin",
                null,
                SystemRoles.Admin)).Status);
        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.CreatePendingAsync(
                editor.Id,
                "editor-forged-user",
                null,
                SystemRoles.Viewer)).Status);
        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.CreatePendingAsync(
                viewer.Id,
                "viewer-forged-user",
                null,
                SystemRoles.Editor)).Status);

        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.ChangeRoleAsync(
                admin.Id,
                ownerId,
                await GetConcurrencyStampAsync(provider, ownerId),
                SystemRoles.Viewer)).Status);
        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.DisableAsync(
                admin.Id,
                admin.Id,
                admin.ConcurrencyStamp)).Status);
        Assert.Equal(
            UserLifecycleStatus.Forbidden,
            (await service.ChangeRoleAsync(
                admin.Id,
                admin.Id,
                admin.ConcurrencyStamp,
                SystemRoles.Editor)).Status);
        Assert.Equal(
            UserLifecycleStatus.SoleOwnerProtected,
            (await service.DisableAsync(
                ownerId,
                ownerId,
                await GetConcurrencyStampAsync(provider, ownerId))).Status);
        Assert.Equal(
            UserLifecycleStatus.SoleOwnerProtected,
            (await service.DeleteAsync(
                ownerId,
                ownerId,
                await GetConcurrencyStampAsync(provider, ownerId))).Status);
        Assert.Equal(
            UserLifecycleStatus.SoleOwnerProtected,
            (await service.ChangeRoleAsync(
                ownerId,
                ownerId,
                await GetConcurrencyStampAsync(provider, ownerId),
                SystemRoles.Admin)).Status);
    }

    [Fact]
    public async Task ActivationIsSingleUseAtomicAndUsesTimeProvider()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "pending-local",
            SystemRoles.Editor);
        string raw = pending.OneTimeActivationCapability!;
        Assert.DoesNotContain(raw, pending.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UserPassword, pending.ToString(), StringComparison.Ordinal);

        Task<AccountActivationResult>[] attempts =
        [
            ActivateInNewScopeAsync(provider, raw, UserPassword),
            ActivateInNewScopeAsync(provider, raw, UserPassword),
        ];
        AccountActivationResult[] results = await Task.WhenAll(attempts);
        Assert.Single(
            results,
            result => result.Status == AccountActivationStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status == AccountActivationStatus.Invalid);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(
            candidate => candidate.Id == pending.TargetUserId);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync(
            candidate => candidate.Purpose == CapabilityPurpose.ActivateUser);
        Assert.True(user.IsEnabled);
        Assert.Equal(time.GetUtcNow(), user.ActivatedAtUtc);
        Assert.Equal(time.GetUtcNow(), capability.UsedAtUtc);
        Assert.Null(capability.ActiveSlot);
        Assert.NotNull(user.PasswordHash);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.user-activated"));
        AssertSecretAbsentFromDatabaseFiles(data.Path, raw);
        AssertSecretAbsentFromDatabaseFiles(data.Path, UserPassword);
    }

    [Fact]
    public async Task ExpiredActivationIsRejectedAndRegenerationRevokesPriorCapability()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2041, 2, 3, 4, 5, 6, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "pending-local",
            SystemRoles.Viewer);
        string original = pending.OneTimeActivationCapability!;
        time.Advance(TimeSpan.FromHours(24));

        Assert.Equal(
            AccountActivationStatus.Invalid,
            (await ActivateInNewScopeAsync(provider, original, UserPassword)).Status);
        UserLifecycleResult replacement;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            replacement = await scope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .RegenerateActivationAsync(
                    ownerId,
                    pending.TargetUserId!.Value,
                    pending.ConcurrencyStamp!);
        }

        Assert.Equal(UserLifecycleStatus.Succeeded, replacement.Status);
        Assert.NotEqual(original, replacement.OneTimeActivationCapability);
        Assert.Equal(
            AccountActivationStatus.Invalid,
            (await ActivateInNewScopeAsync(provider, original, UserPassword)).Status);
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await ActivateInNewScopeAsync(
                provider,
                replacement.OneTimeActivationCapability!,
                UserPassword)).Status);

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability[] capabilities = (await db.SecurityCapabilities
                .Where(capability => capability.Purpose == CapabilityPurpose.ActivateUser)
                .ToArrayAsync())
            .OrderBy(capability => capability.CreatedAtUtc)
            .ToArray();
        Assert.Equal(2, capabilities.Length);
        Assert.Equal(time.GetUtcNow(), capabilities[0].RevokedAtUtc);
        Assert.Null(capabilities[0].ActiveSlot);
        Assert.NotNull(capabilities[1].UsedAtUtc);
    }

    [Fact]
    public async Task ConcurrentActivationRegenerationLeavesOneUsableCapability()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "regeneration-race",
            SystemRoles.Editor);

        UserLifecycleResult[] replacements = await Task.WhenAll(
            RegenerateInNewScopeAsync(
                provider,
                ownerId,
                pending.TargetUserId!.Value,
                pending.ConcurrencyStamp!),
            RegenerateInNewScopeAsync(
                provider,
                ownerId,
                pending.TargetUserId.Value,
                pending.ConcurrencyStamp!));
        Assert.All(
            replacements,
            result => Assert.Equal(UserLifecycleStatus.Succeeded, result.Status));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability active = await db.SecurityCapabilities.SingleAsync(
            capability =>
                capability.Purpose == CapabilityPurpose.ActivateUser
                && capability.ActiveSlot != null);
        UserLifecycleResult usable = Assert.Single(
            replacements,
            result => Hash(result.OneTimeActivationCapability!)
                .SequenceEqual(active.TokenHash));
        UserLifecycleResult revoked = Assert.Single(
            replacements,
            result => result != usable);
        Assert.Equal(
            AccountActivationStatus.Invalid,
            (await ActivateInNewScopeAsync(
                provider,
                revoked.OneTimeActivationCapability!,
                UserPassword)).Status);
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await ActivateInNewScopeAsync(
                provider,
                usable.OneTimeActivationCapability!,
                UserPassword)).Status);
    }

    [Fact]
    public async Task ActivationAndRecoveryCapabilitiesAreStrictlyPurposeBound()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "purpose-bound-user",
            SystemRoles.Editor);
        string recovery = CreateCapability("purpose-bound-recovery");
        await using (AsyncServiceScope issueScope = provider.CreateAsyncScope())
        {
            await issueScope.ServiceProvider
                .GetRequiredService<OwnerRecoveryIssuer>()
                .IssueAsync(Hash(recovery));
        }

        Assert.Equal(
            AccountActivationStatus.Invalid,
            (await ActivateInNewScopeAsync(provider, recovery, UserPassword)).Status);
        await using (AsyncServiceScope recoveryScope = provider.CreateAsyncScope())
        {
            Assert.Equal(
                OwnerRecoveryStatus.Invalid,
                (await recoveryScope.ServiceProvider
                    .GetRequiredService<OwnerRecoveryService>()
                    .CompleteAsync(
                        pending.OneTimeActivationCapability,
                        UserPassword)).Status);
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.All(
            await db.SecurityCapabilities
                .Where(
                    capability =>
                        capability.Purpose == CapabilityPurpose.ActivateUser
                        || capability.Purpose == CapabilityPurpose.RecoverOwner)
                .ToArrayAsync(),
            capability =>
            {
                Assert.Null(capability.UsedAtUtc);
                Assert.NotNull(capability.ActiveSlot);
            });
    }

    [Fact]
    public async Task ConcurrentRoleChangesUseTheUserConcurrencyStamp()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "role-target",
            SystemRoles.Viewer);

        Task<UserLifecycleResult>[] changes =
        [
            ChangeRoleInNewScopeAsync(
                provider,
                ownerId,
                target.Id,
                target.ConcurrencyStamp,
                SystemRoles.Editor),
            ChangeRoleInNewScopeAsync(
                provider,
                ownerId,
                target.Id,
                target.ConcurrencyStamp,
                SystemRoles.Admin),
        ];
        UserLifecycleResult[] results = await Task.WhenAll(changes);
        Assert.Single(
            results,
            result => result.Status == UserLifecycleStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status == UserLifecycleStatus.Conflict);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await users.FindByIdAsync(target.Id.ToString())
            ?? throw new InvalidOperationException();
        Assert.Single(await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task RoleAndDisablementInvalidateSessionsAndRejectStaleUpdates()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "stamp-target",
            SystemRoles.Editor);
        string oldSecurityStamp = await GetSecurityStampAsync(provider, target.Id);

        UserLifecycleResult roleChange = await ChangeRoleInNewScopeAsync(
            provider,
            ownerId,
            target.Id,
            target.ConcurrencyStamp,
            SystemRoles.Viewer);
        Assert.Equal(UserLifecycleStatus.Succeeded, roleChange.Status);
        Assert.NotEqual(oldSecurityStamp, await GetSecurityStampAsync(provider, target.Id));
        Assert.Equal(
            UserLifecycleStatus.Conflict,
            (await DisableInNewScopeAsync(
                provider,
                ownerId,
                target.Id,
                target.ConcurrencyStamp)).Status);

        string beforeDisableStamp = await GetSecurityStampAsync(provider, target.Id);
        UserLifecycleResult disabled = await DisableInNewScopeAsync(
            provider,
            ownerId,
            target.Id,
            roleChange.ConcurrencyStamp!);
        Assert.Equal(UserLifecycleStatus.Succeeded, disabled.Status);
        Assert.NotEqual(beforeDisableStamp, await GetSecurityStampAsync(provider, target.Id));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ApplicationUser user = await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .SingleAsync(candidate => candidate.Id == target.Id);
        Assert.False(user.IsEnabled);
    }

    [Fact]
    public async Task DeletionPreservesCapabilityAndAuditHistory()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "delete-target",
            SystemRoles.Viewer);

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            UserLifecycleResult deleted = await operationScope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .DeleteAsync(
                    ownerId,
                    pending.TargetUserId!.Value,
                    pending.ConcurrencyStamp!);
            Assert.Equal(UserLifecycleStatus.Succeeded, deleted.Status);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.Id == pending.TargetUserId));
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync(
            candidate => candidate.Purpose == CapabilityPurpose.ActivateUser);
        Assert.Null(capability.SubjectUserId);
        Assert.Null(capability.ActiveSlot);
        Assert.NotNull(capability.RevokedAtUtc);
        Assert.Null(capability.CreatedByUserId == pending.TargetUserId
            ? capability.CreatedByUserId
            : null);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record =>
                    record.EventCode == "identity.user-deleted"
                    && record.TargetUserId == pending.TargetUserId));
    }

    [Fact]
    public async Task RequiredDeleteAuditFailureRestoresUserAndActiveCapability()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            "delete-audit-rollback",
            SystemRoles.Viewer);
        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            await triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .Database
                .ExecuteSqlRawAsync(
                    """
                    CREATE TRIGGER fail_delete_audit
                    BEFORE INSERT ON AuditRecords
                    WHEN NEW.EventCode = 'identity.user-deleted'
                    BEGIN SELECT RAISE(ABORT, 'delete audit failure'); END;
                    """);
        }

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DeleteAsync(
                        ownerId,
                        pending.TargetUserId!.Value,
                        pending.ConcurrencyStamp!));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.True(await db.Users.AnyAsync(user => user.Id == pending.TargetUserId));
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync(
            candidate => candidate.Purpose == CapabilityPurpose.ActivateUser);
        Assert.Equal(pending.TargetUserId, capability.SubjectUserId);
        Assert.Null(capability.RevokedAtUtc);
        Assert.NotNull(capability.ActiveSlot);
        Assert.Equal(
            0,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.user-deleted"));
    }

    [Fact]
    public async Task RequiredRoleAuditFailureRestoresRoleAndSecurityStamp()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "role-audit-rollback",
            SystemRoles.Viewer);
        string originalSecurityStamp = await GetSecurityStampAsync(provider, target.Id);
        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            await triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>()
                .Database
                .ExecuteSqlRawAsync(
                    """
                    CREATE TRIGGER fail_role_audit
                    BEFORE INSERT ON AuditRecords
                    WHEN NEW.EventCode = 'identity.user-role-changed'
                    BEGIN SELECT RAISE(ABORT, 'role audit failure'); END;
                    """);
        }

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .ChangeRoleAsync(
                        ownerId,
                        target.Id,
                        target.ConcurrencyStamp,
                        SystemRoles.Editor));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        UserManager<ApplicationUser> users = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await users.FindByIdAsync(target.Id.ToString())
            ?? throw new InvalidOperationException();
        Assert.Equal([SystemRoles.Viewer], await users.GetRolesAsync(user));
        Assert.Equal(originalSecurityStamp, user.SecurityStamp);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("capability")]
    [InlineData("audit")]
    public async Task PendingCreationFailureRollsBackIdentityCapabilityAndAudit(string step)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                step switch
                {
                    "user" => """
                        CREATE TRIGGER fail_lifecycle BEFORE INSERT ON AspNetUsers
                        BEGIN SELECT RAISE(ABORT, 'user failure'); END;
                        """,
                    "capability" => """
                        CREATE TRIGGER fail_lifecycle BEFORE INSERT ON SecurityCapabilities
                        BEGIN SELECT RAISE(ABORT, 'capability failure'); END;
                        """,
                    "audit" => """
                        CREATE TRIGGER fail_lifecycle BEFORE INSERT ON AuditRecords
                        BEGIN SELECT RAISE(ABORT, 'audit failure'); END;
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(step)),
                });
        }

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .CreatePendingAsync(
                        ownerId,
                        "rollback-target",
                        null,
                        SystemRoles.Editor));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(1, await verificationDb.Users.CountAsync());
        Assert.Equal(
            0,
            await verificationDb.SecurityCapabilities.CountAsync(
                capability => capability.Purpose == CapabilityPurpose.ActivateUser));
        Assert.Equal(
            0,
            await verificationDb.AuditRecords.CountAsync(
                record => record.EventCode == "identity.pending-user-created"));
    }

    [Fact]
    public async Task ActivationAuditFailureRollsBackPasswordActivationAndConsumption()
    {
        using TestDataDirectory data = new();
        UserLifecycleResult pending;
        Guid ownerId;
        await using (ServiceProvider initialProvider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(initialProvider);
            ownerId = await InitializeOwnerAsync(initialProvider);
            pending = await CreatePendingAsync(
                initialProvider,
                ownerId,
                "audit-rollback",
                SystemRoles.Editor);
        }

        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddScoped<IAuditWriter, FailingActivationAuditWriter>();
            });
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => scope.ServiceProvider
                    .GetRequiredService<AccountActivationService>()
                    .ActivateAsync(
                        pending.OneTimeActivationCapability,
                        UserPassword));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(
            candidate => candidate.Id == pending.TargetUserId);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync(
            candidate => candidate.Purpose == CapabilityPurpose.ActivateUser);
        Assert.False(user.IsEnabled);
        Assert.Null(user.ActivatedAtUtc);
        Assert.Null(user.PasswordHash);
        Assert.Null(capability.UsedAtUtc);
        Assert.NotNull(capability.ActiveSlot);
    }

    private static async Task<Guid> InitializeOwnerAsync(ServiceProvider provider)
    {
        string raw = CreateCapability("lifecycle-owner-bootstrap");
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(Hash(raw));
        InitialOwnerSetupResult result = await scope.ServiceProvider
            .GetRequiredService<InitialOwnerSetupService>()
            .CreateAsync(
                new InitialOwnerSetupRequest(
                    raw,
                    "owner-local",
                    null,
                    OwnerPassword));
        Assert.Equal(InitialOwnerSetupStatus.Succeeded, result.Status);
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Where(user => user.UserName == "owner-local")
            .Select(user => user.Id)
            .SingleAsync();
    }

    private static async Task<UserLifecycleResult> CreatePendingAsync(
        ServiceProvider provider,
        Guid ownerId,
        string userName,
        string role)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        UserLifecycleResult result = await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .CreatePendingAsync(ownerId, userName, null, role);
        Assert.Equal(UserLifecycleStatus.Succeeded, result.Status);
        Assert.NotNull(result.OneTimeActivationCapability);
        return result;
    }

    private static async Task<CreatedUser> CreateAndActivateAsync(
        ServiceProvider provider,
        Guid ownerId,
        string userName,
        string role)
    {
        UserLifecycleResult pending = await CreatePendingAsync(
            provider,
            ownerId,
            userName,
            role);
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await ActivateInNewScopeAsync(
                provider,
                pending.OneTimeActivationCapability!,
                UserPassword)).Status);
        return new(
            pending.TargetUserId!.Value,
            await GetConcurrencyStampAsync(provider, pending.TargetUserId.Value));
    }

    private static async Task<AccountActivationResult> ActivateInNewScopeAsync(
        ServiceProvider provider,
        string rawCapability,
        string password)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<AccountActivationService>()
            .ActivateAsync(rawCapability, password);
    }

    private static async Task<UserLifecycleResult> ChangeRoleInNewScopeAsync(
        ServiceProvider provider,
        Guid actorId,
        Guid targetId,
        string concurrencyStamp,
        string role)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .ChangeRoleAsync(actorId, targetId, concurrencyStamp, role);
    }

    private static async Task<UserLifecycleResult> RegenerateInNewScopeAsync(
        ServiceProvider provider,
        Guid actorId,
        Guid targetId,
        string concurrencyStamp)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .RegenerateActivationAsync(
                actorId,
                targetId,
                concurrencyStamp);
    }

    private static async Task<UserLifecycleResult> DisableInNewScopeAsync(
        ServiceProvider provider,
        Guid actorId,
        Guid targetId,
        string concurrencyStamp)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .DisableAsync(actorId, targetId, concurrencyStamp);
    }

    private static async Task<string> GetConcurrencyStampAsync(
        ServiceProvider provider,
        Guid userId)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.ConcurrencyStamp!)
            .SingleAsync();
    }

    private static async Task<string> GetSecurityStampAsync(
        ServiceProvider provider,
        Guid userId)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SecurityStamp!)
            .SingleAsync();
    }

    private static string CreateCapability(string seed) =>
        WebEncoders.Base64UrlEncode(Hash(seed));

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static void AssertSecretAbsentFromDatabaseFiles(
        string dataDirectory,
        string secret)
    {
        foreach (string path in Directory.EnumerateFiles(
            dataDirectory,
            "creator-toolkit.db*",
            SearchOption.TopDirectoryOnly))
        {
            Assert.DoesNotContain(
                secret,
                Encoding.Latin1.GetString(File.ReadAllBytes(path)),
                StringComparison.Ordinal);
        }
    }

    private sealed record CreatedUser(Guid Id, string ConcurrencyStamp);

    private sealed class FailingActivationAuditWriter(
        CreatorToolkitDbContext dbContext,
        TimeProvider timeProvider) : IAuditWriter
    {
        public Task WriteAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (auditEvent.EventCode == AuditEventCode.UserActivated)
            {
                throw new InvalidOperationException("Synthetic activation audit failure.");
            }

            dbContext.AuditRecords.Add(
                AuditRecord.Create(
                    auditEvent,
                    timeProvider.GetUtcNow().ToUniversalTime()));
            return Task.CompletedTask;
        }
    }
}
