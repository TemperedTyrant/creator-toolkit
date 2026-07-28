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
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Commands;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Identity;

public sealed class OwnershipAndRecoveryTests
{
    private const string OwnerPassword = "mild river orbit velvet canyon";
    private const string UserPassword = "silver meadow lantern compass";
    private const string RecoveredPassword = "harbor cedar quiet aurora";

    [Fact]
    public async Task OwnershipTransferRequiresPasswordAndCommitsEveryInvariant()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "new-owner",
            SystemRoles.Editor);
        (long revision, Guid originalOwner) = await GetOwnershipAsync(provider);

        await using (AsyncServiceScope rejectedScope = provider.CreateAsyncScope())
        {
            OwnershipTransferResult rejected = await rejectedScope.ServiceProvider
                .GetRequiredService<OwnershipTransferService>()
                .TransferAsync(
                    ownerId,
                    target.Id,
                    "incorrect-current-password",
                    revision,
                    target.ConcurrencyStamp);
            Assert.Equal(OwnershipTransferStatus.InvalidPassword, rejected.Status);
        }
        Assert.Equal((revision, originalOwner), await GetOwnershipAsync(provider));

        string oldOwnerStamp = await GetSecurityStampAsync(provider, ownerId);
        string oldTargetStamp = await GetSecurityStampAsync(provider, target.Id);
        await using (AsyncServiceScope transferScope = provider.CreateAsyncScope())
        {
            OwnershipTransferResult result = await transferScope.ServiceProvider
                .GetRequiredService<OwnershipTransferService>()
                .TransferAsync(
                    ownerId,
                    target.Id,
                    OwnerPassword,
                    revision,
                    target.ConcurrencyStamp);
            Assert.Equal(OwnershipTransferStatus.Succeeded, result.Status);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        Ownership ownership = await db.Ownerships.SingleAsync();
        ApplicationUser formerOwner = await users.FindByIdAsync(ownerId.ToString())
            ?? throw new InvalidOperationException();
        ApplicationUser newOwner = await users.FindByIdAsync(target.Id.ToString())
            ?? throw new InvalidOperationException();
        Assert.Equal(target.Id, ownership.OwnerUserId);
        Assert.Equal([SystemRoles.Admin], await users.GetRolesAsync(formerOwner));
        Assert.Equal([SystemRoles.Owner], await users.GetRolesAsync(newOwner));
        Assert.NotEqual(oldOwnerStamp, formerOwner.SecurityStamp);
        Assert.NotEqual(oldTargetStamp, newOwner.SecurityStamp);
        Assert.Equal(
            1,
            await db.UserRoles.Join(
                    db.Roles.Where(role => role.Name == SystemRoles.Owner),
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (userRole, _) => userRole)
                .CountAsync());
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.ownership-transferred"));
    }

    [Fact]
    public async Task CompetingOwnershipTransfersProduceExactlyOneOwner()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser first = await CreateAndActivateAsync(
            provider,
            ownerId,
            "first-target",
            SystemRoles.Editor);
        CreatedUser second = await CreateAndActivateAsync(
            provider,
            ownerId,
            "second-target",
            SystemRoles.Admin);
        (long revision, _) = await GetOwnershipAsync(provider);

        OwnershipTransferResult[] results = await Task.WhenAll(
            TransferInNewScopeAsync(provider, ownerId, first, revision),
            TransferInNewScopeAsync(provider, ownerId, second, revision));
        Assert.Single(
            results,
            result => result.Status == OwnershipTransferStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status is OwnershipTransferStatus.Forbidden
                or OwnershipTransferStatus.Conflict);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Guid ownerRoleId = await db.Roles
            .Where(role => role.Name == SystemRoles.Owner)
            .Select(role => role.Id)
            .SingleAsync();
        Guid roleOwnerId = await db.UserRoles
            .Where(userRole => userRole.RoleId == ownerRoleId)
            .Select(userRole => userRole.UserId)
            .SingleAsync();
        Assert.Equal((await db.Ownerships.SingleAsync()).OwnerUserId, roleOwnerId);
    }

    [Fact]
    public async Task TargetDisabledBeforeTransferFailsWithoutChangingOwnership()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "disabled-target",
            SystemRoles.Editor);
        (long revision, _) = await GetOwnershipAsync(provider);
        await using (AsyncServiceScope disableScope = provider.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await disableScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DisableAsync(
                        ownerId,
                        target.Id,
                        target.ConcurrencyStamp)).Status);
        }

        await using (AsyncServiceScope transferScope = provider.CreateAsyncScope())
        {
            OwnershipTransferResult result = await transferScope.ServiceProvider
                .GetRequiredService<OwnershipTransferService>()
                .TransferAsync(
                    ownerId,
                    target.Id,
                    OwnerPassword,
                    revision,
                    target.ConcurrencyStamp);
            Assert.Equal(OwnershipTransferStatus.Conflict, result.Status);
        }
        Assert.Equal(ownerId, (await GetOwnershipAsync(provider)).OwnerId);
    }

    [Theory]
    [InlineData("target-role-remove")]
    [InlineData("target-role-add")]
    [InlineData("owner-role-remove")]
    [InlineData("owner-role-add")]
    [InlineData("ownership")]
    [InlineData("target-stamp")]
    [InlineData("owner-stamp")]
    [InlineData("audit")]
    public async Task OwnershipTransferFailureRollsBackRolesOwnershipAndStamps(string step)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        CreatedUser target = await CreateAndActivateAsync(
            provider,
            ownerId,
            "rollback-target",
            SystemRoles.Viewer);
        (long revision, _) = await GetOwnershipAsync(provider);
        string ownerStamp = await GetSecurityStampAsync(provider, ownerId);
        string targetStamp = await GetSecurityStampAsync(provider, target.Id);

        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                step switch
                {
                    "target-role-remove" => """
                        CREATE TRIGGER fail_transfer BEFORE DELETE ON AspNetUserRoles
                        WHEN OLD.UserId = (
                            SELECT Id FROM AspNetUsers WHERE UserName = 'rollback-target'
                        )
                        BEGIN SELECT RAISE(ABORT, 'target role removal failure'); END;
                        """,
                    "target-role-add" => """
                        CREATE TRIGGER fail_transfer BEFORE INSERT ON AspNetUserRoles
                        WHEN NEW.UserId = (
                            SELECT Id FROM AspNetUsers WHERE UserName = 'rollback-target'
                        )
                        BEGIN SELECT RAISE(ABORT, 'target role insertion failure'); END;
                        """,
                    "owner-role-remove" => """
                        CREATE TRIGGER fail_transfer BEFORE DELETE ON AspNetUserRoles
                        WHEN OLD.UserId = (
                            SELECT Id FROM AspNetUsers WHERE UserName = 'owner-local'
                        )
                        BEGIN SELECT RAISE(ABORT, 'owner role removal failure'); END;
                        """,
                    "owner-role-add" => """
                        CREATE TRIGGER fail_transfer BEFORE INSERT ON AspNetUserRoles
                        WHEN NEW.UserId = (
                            SELECT Id FROM AspNetUsers WHERE UserName = 'owner-local'
                        )
                        BEGIN SELECT RAISE(ABORT, 'owner role insertion failure'); END;
                        """,
                    "ownership" => """
                        CREATE TRIGGER fail_transfer BEFORE UPDATE OF OwnerUserId ON Ownerships
                        BEGIN SELECT RAISE(ABORT, 'ownership failure'); END;
                        """,
                    "target-stamp" => """
                        CREATE TRIGGER fail_transfer
                        BEFORE UPDATE OF SecurityStamp ON AspNetUsers
                        WHEN OLD.UserName = 'rollback-target'
                        BEGIN SELECT RAISE(ABORT, 'target stamp failure'); END;
                        """,
                    "owner-stamp" => """
                        CREATE TRIGGER fail_transfer
                        BEFORE UPDATE OF SecurityStamp ON AspNetUsers
                        WHEN OLD.UserName = 'owner-local'
                        BEGIN SELECT RAISE(ABORT, 'owner stamp failure'); END;
                        """,
                    "audit" => """
                        CREATE TRIGGER fail_transfer BEFORE INSERT ON AuditRecords
                        BEGIN SELECT RAISE(ABORT, 'audit failure'); END;
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(step)),
                });
        }

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<OwnershipTransferService>()
                    .TransferAsync(
                        ownerId,
                        target.Id,
                        OwnerPassword,
                        revision,
                        target.ConcurrencyStamp));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        UserManager<ApplicationUser> users = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        CreatorToolkitDbContext verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        ApplicationUser owner = await users.FindByIdAsync(ownerId.ToString())
            ?? throw new InvalidOperationException();
        ApplicationUser targetUser = await users.FindByIdAsync(target.Id.ToString())
            ?? throw new InvalidOperationException();
        Assert.Equal([SystemRoles.Owner], await users.GetRolesAsync(owner));
        Assert.Equal([SystemRoles.Viewer], await users.GetRolesAsync(targetUser));
        Assert.Equal(ownerId, (await verificationDb.Ownerships.SingleAsync()).OwnerUserId);
        Assert.Equal(ownerStamp, owner.SecurityStamp);
        Assert.Equal(targetStamp, targetUser.SecurityStamp);
    }

    [Fact]
    public async Task ResetOwnerCommandRequiresConfirmationRunsBesideHostAndStoresOnlyHash()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        string originalStamp = await GetSecurityStampAsync(provider, ownerId);
        CreatorToolkitOptions options = new(data.Path, null, [], []);

        StringWriter cancelledOutput = new();
        StringWriter cancelledError = new();
        Assert.Equal(
            1,
            await ResetOwnerCommand.RunAsync(
                provider,
                options,
                new StringReader("not confirmed"),
                cancelledOutput,
                cancelledError,
                nonInteractive: false));
        Assert.Contains("cancelled", cancelledError.ToString(), StringComparison.OrdinalIgnoreCase);

        await using ApplicationHostLease hostLease = await provider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();
        StringWriter output = new();
        StringWriter error = new();
        Assert.Equal(
            0,
            await ResetOwnerCommand.RunAsync(
                provider,
                options,
                TextReader.Null,
                output,
                error,
                nonInteractive: true));
        string[] lines = output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("/Account/RecoverOwner", lines[0]);
        Assert.Equal(43, lines[1].Length);
        Assert.Empty(error.ToString());
        Assert.DoesNotContain(logs, entry => entry.Contains(lines[1], StringComparison.Ordinal));
        AssertSecretAbsentFromDatabaseFiles(data.Path, lines[1]);
        Assert.NotEqual(originalStamp, await GetSecurityStampAsync(provider, ownerId));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        SecurityCapability capability = await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .SecurityCapabilities
            .SingleAsync(candidate => candidate.Purpose == CapabilityPurpose.RecoverOwner);
        Assert.Equal(Hash(lines[1]), capability.TokenHash);
        Assert.Equal(ownerId, capability.SubjectUserId);
        Assert.Equal(
            capability.CreatedAtUtc.AddMinutes(30),
            capability.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("stamp")]
    [InlineData("audit")]
    public async Task RecoveryIssuanceFailureRollsBackReplacementAndSessionInvalidation(
        string step)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        string original = CreateCapability("recovery-issuance-original");
        await IssueRecoveryAsync(provider, original);
        string originalStamp = await GetSecurityStampAsync(provider, ownerId);

        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                step switch
                {
                    "capability" => """
                        CREATE TRIGGER fail_recovery_issuance
                        BEFORE INSERT ON SecurityCapabilities
                        WHEN NEW.Purpose = 'RecoverOwner'
                        BEGIN SELECT RAISE(ABORT, 'capability failure'); END;
                        """,
                    "stamp" => """
                        CREATE TRIGGER fail_recovery_issuance
                        BEFORE UPDATE OF SecurityStamp ON AspNetUsers
                        BEGIN SELECT RAISE(ABORT, 'stamp failure'); END;
                        """,
                    "audit" => """
                        CREATE TRIGGER fail_recovery_issuance
                        BEFORE INSERT ON AuditRecords
                        WHEN NEW.EventCode = 'identity.owner-recovery-capability-created'
                        BEGIN SELECT RAISE(ABORT, 'audit failure'); END;
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(step)),
                });
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => IssueRecoveryAsync(
                provider,
                CreateCapability($"recovery-issuance-{step}")));

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability capability = await verificationDb.SecurityCapabilities
            .SingleAsync(candidate => candidate.Purpose == CapabilityPurpose.RecoverOwner);
        Assert.Equal(Hash(original), capability.TokenHash);
        Assert.Null(capability.RevokedAtUtc);
        Assert.Null(capability.UsedAtUtc);
        Assert.Equal(SecurityCapability.RecoverOwnerActiveSlot, capability.ActiveSlot);
        Assert.Equal(originalStamp, await GetSecurityStampAsync(provider, ownerId));
        Assert.Equal(
            1,
            await verificationDb.AuditRecords.CountAsync(
                record =>
                    record.EventCode == "identity.owner-recovery-capability-created"));
    }

    [Fact]
    public async Task RecoveryReplacementReplayAndCompletionPreserveOwnership()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2042, 3, 4, 5, 6, 7, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        string first = CreateCapability("first-recovery");
        string second = CreateCapability("second-recovery");
        await IssueRecoveryAsync(provider, first);
        time.Advance(TimeSpan.FromMinutes(1));
        await IssueRecoveryAsync(provider, second);

        Assert.Equal(
            OwnerRecoveryStatus.Invalid,
            (await RecoverInNewScopeAsync(provider, first, RecoveredPassword)).Status);
        Assert.Equal(
            OwnerRecoveryStatus.Succeeded,
            (await RecoverInNewScopeAsync(provider, second, RecoveredPassword)).Status);
        Assert.Equal(
            OwnerRecoveryStatus.Invalid,
            (await RecoverInNewScopeAsync(provider, second, RecoveredPassword)).Status);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser owner = await users.FindByIdAsync(ownerId.ToString())
            ?? throw new InvalidOperationException();
        Assert.True(await users.CheckPasswordAsync(owner, RecoveredPassword));
        Assert.False(await users.CheckPasswordAsync(owner, OwnerPassword));
        Assert.Equal(ownerId, (await db.Ownerships.SingleAsync()).OwnerUserId);
        Assert.Equal([SystemRoles.Owner], await users.GetRolesAsync(owner));
        SecurityCapability[] capabilities = (await db.SecurityCapabilities
                .Where(capability => capability.Purpose == CapabilityPurpose.RecoverOwner)
                .ToArrayAsync())
            .OrderBy(capability => capability.CreatedAtUtc)
            .ToArray();
        Assert.Equal(2, capabilities.Length);
        Assert.Equal(time.GetUtcNow(), capabilities[0].RevokedAtUtc);
        Assert.NotNull(capabilities[1].UsedAtUtc);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.owner-recovered"));
        AssertSecretAbsentFromDatabaseFiles(data.Path, second);
        AssertSecretAbsentFromDatabaseFiles(data.Path, RecoveredPassword);
    }

    [Fact]
    public async Task ConcurrentRecoveryIssuanceLeavesOneActiveCapability()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await InitializeOwnerAsync(provider);
        string first = CreateCapability("concurrent-recovery-first");
        string second = CreateCapability("concurrent-recovery-second");

        await Task.WhenAll(
            IssueRecoveryAsync(provider, first),
            IssueRecoveryAsync(provider, second));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability[] capabilities = await db.SecurityCapabilities
            .Where(capability => capability.Purpose == CapabilityPurpose.RecoverOwner)
            .ToArrayAsync();
        Assert.Equal(2, capabilities.Length);
        Assert.Single(capabilities, capability => capability.ActiveSlot is not null);
        Assert.Single(capabilities, capability => capability.RevokedAtUtc is not null);
    }

    [Fact]
    public async Task RecoveryExpiresAtThirtyMinutesAndAuditFailureRollsBackReset()
    {
        using TestDataDirectory expiredData = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2043, 4, 5, 6, 7, 8, TimeSpan.Zero));
        await using (ServiceProvider expiredProvider = TestServices.Create(
            expiredData.Path,
            timeProvider: time))
        {
            await TestServices.InitializeAsync(expiredProvider);
            await InitializeOwnerAsync(expiredProvider);
            string expired = CreateCapability("expired-recovery");
            await IssueRecoveryAsync(expiredProvider, expired);
            time.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(
                OwnerRecoveryStatus.Invalid,
                (await RecoverInNewScopeAsync(
                    expiredProvider,
                    expired,
                    RecoveredPassword)).Status);
        }

        using TestDataDirectory rollbackData = new();
        string raw = CreateCapability("recovery-audit-rollback");
        await using (ServiceProvider initialProvider = TestServices.Create(rollbackData.Path))
        {
            await TestServices.InitializeAsync(initialProvider);
            await InitializeOwnerAsync(initialProvider);
            await IssueRecoveryAsync(initialProvider, raw);
        }

        await using ServiceProvider provider = TestServices.Create(
            rollbackData.Path,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddScoped<IAuditWriter, FailingRecoveryAuditWriter>();
            });
        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<OwnerRecoveryService>()
                    .CompleteAsync(raw, RecoveredPassword));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        UserManager<ApplicationUser> users = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser owner = await users.Users.SingleAsync();
        Assert.True(await users.CheckPasswordAsync(owner, OwnerPassword));
        Assert.False(await users.CheckPasswordAsync(owner, RecoveredPassword));
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync(
            candidate => candidate.Purpose == CapabilityPurpose.RecoverOwner);
        Assert.Null(capability.UsedAtUtc);
        Assert.NotNull(capability.ActiveSlot);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("capability")]
    [InlineData("audit")]
    public async Task RecoveryCompletionFailureRollsBackPasswordCapabilityStampAndAudit(
        string step)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid ownerId = await InitializeOwnerAsync(provider);
        string raw = CreateCapability($"recovery-completion-{step}");
        await IssueRecoveryAsync(provider, raw);
        string issuedStamp = await GetSecurityStampAsync(provider, ownerId);

        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext db = triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                step switch
                {
                    "password" => """
                        CREATE TRIGGER fail_recovery_completion
                        BEFORE UPDATE OF PasswordHash ON AspNetUsers
                        BEGIN SELECT RAISE(ABORT, 'password failure'); END;
                        """,
                    "capability" => """
                        CREATE TRIGGER fail_recovery_completion
                        BEFORE UPDATE OF UsedAtUtc ON SecurityCapabilities
                        WHEN OLD.Purpose = 'RecoverOwner'
                        BEGIN SELECT RAISE(ABORT, 'capability failure'); END;
                        """,
                    "audit" => """
                        CREATE TRIGGER fail_recovery_completion
                        BEFORE INSERT ON AuditRecords
                        WHEN NEW.EventCode = 'identity.owner-recovered'
                        BEGIN SELECT RAISE(ABORT, 'audit failure'); END;
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(step)),
                });
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => RecoverInNewScopeAsync(provider, raw, RecoveredPassword));

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        UserManager<ApplicationUser> users = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser owner = await users.FindByIdAsync(ownerId.ToString())
            ?? throw new InvalidOperationException();
        SecurityCapability capability = await verificationDb.SecurityCapabilities
            .SingleAsync(candidate => candidate.Purpose == CapabilityPurpose.RecoverOwner);
        Assert.True(await users.CheckPasswordAsync(owner, OwnerPassword));
        Assert.False(await users.CheckPasswordAsync(owner, RecoveredPassword));
        Assert.Equal(issuedStamp, owner.SecurityStamp);
        Assert.Null(capability.UsedAtUtc);
        Assert.Equal(SecurityCapability.RecoverOwnerActiveSlot, capability.ActiveSlot);
        Assert.Equal(0, await verificationDb.UserTokens.CountAsync());
        Assert.Equal(
            0,
            await verificationDb.AuditRecords.CountAsync(
                record => record.EventCode == "identity.owner-recovered"));
    }

    private static async Task<Guid> InitializeOwnerAsync(ServiceProvider provider)
    {
        string raw = CreateCapability("ownership-owner-bootstrap");
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(Hash(raw));
        Assert.Equal(
            InitialOwnerSetupStatus.Succeeded,
            (await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        raw,
                        "owner-local",
                        null,
                        OwnerPassword))).Status);
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Select(user => user.Id)
            .SingleAsync();
    }

    private static async Task<CreatedUser> CreateAndActivateAsync(
        ServiceProvider provider,
        Guid ownerId,
        string userName,
        string role)
    {
        UserLifecycleResult pending;
        await using (AsyncServiceScope createScope = provider.CreateAsyncScope())
        {
            pending = await createScope.ServiceProvider
                .GetRequiredService<UserLifecycleService>()
                .CreatePendingAsync(ownerId, userName, null, role);
        }
        Assert.Equal(UserLifecycleStatus.Succeeded, pending.Status);
        await using (AsyncServiceScope activateScope = provider.CreateAsyncScope())
        {
            Assert.Equal(
                AccountActivationStatus.Succeeded,
                (await activateScope.ServiceProvider
                    .GetRequiredService<AccountActivationService>()
                    .ActivateAsync(
                        pending.OneTimeActivationCapability,
                        UserPassword)).Status);
        }

        return new(
            pending.TargetUserId!.Value,
            await GetConcurrencyStampAsync(provider, pending.TargetUserId.Value));
    }

    private static async Task<OwnershipTransferResult> TransferInNewScopeAsync(
        ServiceProvider provider,
        Guid ownerId,
        CreatedUser target,
        long revision)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<OwnershipTransferService>()
            .TransferAsync(
                ownerId,
                target.Id,
                OwnerPassword,
                revision,
                target.ConcurrencyStamp);
    }

    private static async Task IssueRecoveryAsync(
        ServiceProvider provider,
        string rawCapability)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<OwnerRecoveryIssuer>()
            .IssueAsync(Hash(rawCapability));
    }

    private static async Task<OwnerRecoveryResult> RecoverInNewScopeAsync(
        ServiceProvider provider,
        string rawCapability,
        string password)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<OwnerRecoveryService>()
            .CompleteAsync(rawCapability, password);
    }

    private static async Task<(long Revision, Guid OwnerId)> GetOwnershipAsync(
        ServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Ownership ownership = await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Ownerships
            .AsNoTracking()
            .SingleAsync();
        return (ownership.Revision, ownership.OwnerUserId);
    }

    private static async Task<string> GetConcurrencyStampAsync(
        ServiceProvider provider,
        Guid userId)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
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

    private sealed class FailingRecoveryAuditWriter(
        CreatorToolkitDbContext dbContext,
        TimeProvider timeProvider) : IAuditWriter
    {
        public Task WriteAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (auditEvent.EventCode == AuditEventCode.OwnerRecovered)
            {
                throw new InvalidOperationException("Synthetic recovery audit failure.");
            }

            dbContext.AuditRecords.Add(
                AuditRecord.Create(
                    auditEvent,
                    timeProvider.GetUtcNow().ToUniversalTime()));
            return Task.CompletedTask;
        }
    }
}
