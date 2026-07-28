using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Commands;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Setup;

public sealed class BootstrapAndInitialOwnerTests
{
    private const string ValidPassword = "mild river orbit velvet canyon";

    [Fact]
    public void SetupRequestStringRepresentationCannotExposeSubmittedSecrets()
    {
        const string capability = "capability-marker";
        const string password = "password-marker";
        InitialOwnerSetupRequest request = new(
            capability,
            "owner-local",
            null,
            password);

        Assert.Equal(nameof(InitialOwnerSetupRequest), request.ToString());
        Assert.DoesNotContain(capability, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityStoresUseTheCallerScopedDbContext()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        IUserStore<ApplicationUser> userStore = scope.ServiceProvider
            .GetRequiredService<IUserStore<ApplicationUser>>();
        IRoleStore<IdentityRole<Guid>> roleStore = scope.ServiceProvider
            .GetRequiredService<IRoleStore<IdentityRole<Guid>>>();

        Assert.Same(dbContext, GetStoreContext(userStore));
        Assert.Same(dbContext, GetStoreContext(roleStore));
    }

    [Fact]
    public async Task IssuanceUsesShortLockReplacesActiveCapabilityAndUsesTimeProvider()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2035, 4, 5, 6, 7, 8, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        await using ApplicationHostLease hostLease = await provider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            BootstrapCapabilityIssuer issuer = scope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>();
            Assert.Equal(
                BootstrapCapabilityIssueResult.Created,
                await issuer.IssueAsync(Hash("first-capability")));
        }

        time.Advance(TimeSpan.FromMinutes(2));
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            BootstrapCapabilityIssuer issuer = scope.ServiceProvider
                .GetRequiredService<BootstrapCapabilityIssuer>();
            Assert.Equal(
                BootstrapCapabilityIssueResult.Replaced,
                await issuer.IssueAsync(Hash("second-capability")));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability[] capabilities = (await db.SecurityCapabilities.ToArrayAsync())
            .OrderBy(capability => capability.CreatedAtUtc)
            .ToArray();

        Assert.Equal(2, capabilities.Length);
        Assert.Equal(time.GetUtcNow().AddMinutes(-2), capabilities[0].CreatedAtUtc);
        Assert.Equal(time.GetUtcNow(), capabilities[0].RevokedAtUtc);
        Assert.Null(capabilities[0].ActiveSlot);
        Assert.Equal(time.GetUtcNow(), capabilities[1].CreatedAtUtc);
        Assert.Equal(time.GetUtcNow().AddMinutes(30), capabilities[1].ExpiresAtUtc);
        Assert.Equal(SecurityCapability.BootstrapOwnerActiveSlot, capabilities[1].ActiveSlot);
        Assert.Equal(
            2,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "security.bootstrap-capability-created"));
    }

    [Fact]
    public async Task InitialOwnerSetupCommitsAllRequiredStateAtomically()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2036, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        string rawCapability = CreateCapability("setup-capability");
        await IssueAsync(provider, rawCapability);

        InitialOwnerSetupResult result;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            result = await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        rawCapability,
                        "owner-local",
                        null,
                        ValidPassword));
        }

        Assert.Equal(InitialOwnerSetupStatus.Succeeded, result.Status);
        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        UserManager<ApplicationUser> users = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await db.Users.SingleAsync();

        Assert.Equal("owner-local", user.UserName);
        Assert.Equal("owner-local", user.DisplayName);
        Assert.Null(user.Email);
        Assert.True(user.IsEnabled);
        Assert.Equal(time.GetUtcNow(), user.CreatedAtUtc);
        Assert.Contains(SystemRoles.Owner, await users.GetRolesAsync(user));
        Assert.Equal(1, await db.Workspaces.CountAsync());
        Assert.Equal(user.Id, (await db.Ownerships.SingleAsync()).OwnerUserId);
        Assert.Equal(
            time.GetUtcNow(),
            (await db.InstallationStates.SingleAsync()).InitializedAtUtc);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.Equal(time.GetUtcNow(), capability.UsedAtUtc);
        Assert.Null(capability.ActiveSlot);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.initial-owner-created"));
        AssertSecretAbsentFromDatabaseFiles(data.Path, rawCapability);
        AssertSecretAbsentFromDatabaseFiles(data.Path, ValidPassword);
    }

    [Fact]
    public async Task DuplicateCapabilityHashRollsBackReplacementState()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        string rawCapability = CreateCapability("duplicate-hash");
        await IssueAsync(provider, rawCapability);

        await using (AsyncServiceScope collisionScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => collisionScope.ServiceProvider
                    .GetRequiredService<BootstrapCapabilityIssuer>()
                    .IssueAsync(Hash(rawCapability)));
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.Null(capability.RevokedAtUtc);
        Assert.Null(capability.UsedAtUtc);
        Assert.Equal(SecurityCapability.BootstrapOwnerActiveSlot, capability.ActiveSlot);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "security.bootstrap-capability-created"));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("role")]
    [InlineData("workspace")]
    [InlineData("ownership")]
    [InlineData("installation")]
    [InlineData("capability")]
    [InlineData("audit")]
    public async Task FailureAtEachInitializationWriteRollsBackAllState(string step)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        string rawCapability = CreateCapability($"failure-{step}");
        await IssueAsync(provider, rawCapability);

        await using (AsyncServiceScope triggerScope = provider.CreateAsyncScope())
        {
            CreatorToolkitDbContext triggerDb = triggerScope.ServiceProvider
                .GetRequiredService<CreatorToolkitDbContext>();
            await triggerDb.Database.ExecuteSqlRawAsync(CreateFailureTrigger(step));
        }

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => operationScope.ServiceProvider
                    .GetRequiredService<InitialOwnerSetupService>()
                    .CreateAsync(
                        new InitialOwnerSetupRequest(
                            rawCapability,
                            "owner-local",
                            null,
                            ValidPassword)));
        }

        await AssertNoPartialInitializationAsync(provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedCapabilitiesAreRejectedWithoutWrites(string submittedCapability)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await IssueAsync(provider, CreateCapability("strict-format"));

        await using (AsyncServiceScope operationScope = provider.CreateAsyncScope())
        {
            InitialOwnerSetupResult result = await operationScope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        submittedCapability,
                        "owner-local",
                        null,
                        ValidPassword));
            Assert.Equal(InitialOwnerSetupStatus.InvalidCapability, result.Status);
        }

        await AssertNoPartialInitializationAsync(provider);
    }

    [Fact]
    public async Task ConcurrentSetupRequestsProduceExactlyOneOwner()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        string rawCapability = CreateCapability("concurrent-setup-capability");
        await IssueAsync(provider, rawCapability);

        Task<InitialOwnerSetupResult>[] operations =
        [
            CreateOwnerInNewScopeAsync(provider, rawCapability, "owner-one"),
            CreateOwnerInNewScopeAsync(provider, rawCapability, "owner-two"),
        ];
        InitialOwnerSetupResult[] results = await Task.WhenAll(operations);

        Assert.Single(
            results,
            result => result.Status == InitialOwnerSetupStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status == InitialOwnerSetupStatus.AlreadyInitialized);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Ownerships.CountAsync());
        Assert.Equal(1, await db.UserRoles.CountAsync());
        Assert.Equal(1, await db.Workspaces.CountAsync());
        Assert.NotNull((await db.InstallationStates.SingleAsync()).InitializedAtUtc);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.NotNull(capability.UsedAtUtc);
        Assert.Null(capability.ActiveSlot);
        Assert.Equal(
            1,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.initial-owner-created"));
    }

    [Fact]
    public async Task CapabilityExpiresAtThirtyMinutesAndCanOnlyBeReplacedByRevocation()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2037, 2, 3, 4, 5, 6, TimeSpan.Zero));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: time);
        await TestServices.InitializeAsync(provider);
        string expiredCapability = CreateCapability("expired-setup-capability");
        await IssueAsync(provider, expiredCapability);
        time.Advance(TimeSpan.FromMinutes(30));

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            InitialOwnerSetupResult expiredResult = await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        expiredCapability,
                        "owner-local",
                        null,
                        ValidPassword));
            Assert.Equal(InitialOwnerSetupStatus.InvalidCapability, expiredResult.Status);
        }
        await AssertNoPartialInitializationAsync(provider);

        string replacementCapability = CreateCapability("replacement-setup-capability");
        await IssueAsync(provider, replacementCapability);

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability[] capabilities = await db.SecurityCapabilities.ToArrayAsync();
        Assert.Equal(2, capabilities.Length);
        SecurityCapability expired = Assert.Single(
            capabilities,
            capability => capability.TokenHash.SequenceEqual(Hash(expiredCapability)));
        SecurityCapability replacement = Assert.Single(
            capabilities,
            capability => capability.TokenHash.SequenceEqual(Hash(replacementCapability)));
        Assert.Equal(time.GetUtcNow(), expired.RevokedAtUtc);
        Assert.Null(expired.ActiveSlot);
        Assert.Equal(
            SecurityCapability.BootstrapOwnerActiveSlot,
            replacement.ActiveSlot);
    }

    [Fact]
    public async Task RequiredAuditFailureRollsBackEveryInitializationChange()
    {
        using TestDataDirectory data = new();
        string rawCapability = CreateCapability("rollback-setup-capability");
        await using (ServiceProvider issuerProvider = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(issuerProvider);
            await IssueAsync(issuerProvider, rawCapability);
        }

        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddScoped<IAuditWriter, FailingAuditWriter>();
            });

        await using AsyncServiceScope operationScope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => operationScope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        rawCapability,
                        "owner-local",
                        null,
                        ValidPassword)));

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = verificationScope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.Users.ToArrayAsync());
        Assert.Empty(await db.Workspaces.ToArrayAsync());
        Assert.Empty(await db.Ownerships.ToArrayAsync());
        Assert.Null((await db.InstallationStates.SingleAsync()).InitializedAtUtc);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.Null(capability.UsedAtUtc);
        Assert.Equal(SecurityCapability.BootstrapOwnerActiveSlot, capability.ActiveSlot);
    }

    [Fact]
    public async Task CommandPrintsRawCapabilityOnlyToDirectOutputAndNeverStoresIt()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(data.Path, logs);
        StringWriter output = new();
        StringWriter error = new();
        CreatorToolkitOptions options = new(data.Path, null, [], []);

        int exitCode = await BootstrapOwnerCommand.RunAsync(
            provider,
            options,
            output,
            error);
        string[] lines = output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, exitCode);
        Assert.Equal("/Setup", lines[0]);
        Assert.Equal(43, lines[1].Length);
        Assert.Empty(error.ToString());
        Assert.DoesNotContain(logs, message => message.Contains(lines[1], StringComparison.Ordinal));
        AssertSecretAbsentFromDatabaseFiles(data.Path, lines[1]);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.Equal(Hash(lines[1]), capability.TokenHash);
    }

    [Fact]
    public async Task CommandRunsWhileTheWebHostSingletonLockIsHeld()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using ApplicationHostLease hostLease = await provider
            .GetRequiredService<ApplicationHostLock>()
            .AcquireAsync();
        StringWriter output = new();

        int exitCode = await BootstrapOwnerCommand.RunAsync(
            provider,
            new CreatorToolkitOptions(data.Path, null, [], []),
            output,
            TextWriter.Null);

        Assert.Equal(0, exitCode);
        string[] lines = output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("/Setup", lines[0]);
        Assert.Equal(43, lines[1].Length);
    }

    [Fact]
    public async Task CommandUsesConfiguredPublicUrlAndPermanentlyRefusesAfterSetup()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        CreatorToolkitOptions options = new(
            data.Path,
            new Uri("https://creator.example/base"),
            [],
            []);
        StringWriter firstOutput = new();

        Assert.Equal(
            0,
            await BootstrapOwnerCommand.RunAsync(
                provider,
                options,
                firstOutput,
                TextWriter.Null));
        string completeUrl = firstOutput.ToString().Trim();
        Assert.StartsWith("https://creator.example/Setup#token=", completeUrl, StringComparison.Ordinal);
        string rawCapability = completeUrl[(completeUrl.IndexOf("#token=", StringComparison.Ordinal) + 7)..];

        InitialOwnerSetupResult setupResult;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            setupResult = await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        rawCapability,
                        "owner-local",
                        null,
                        ValidPassword));
        }
        Assert.Equal(InitialOwnerSetupStatus.Succeeded, setupResult.Status);

        StringWriter refusedOutput = new();
        StringWriter refusedError = new();
        Assert.Equal(
            1,
            await BootstrapOwnerCommand.RunAsync(
                provider,
                options,
                refusedOutput,
                refusedError));
        Assert.Empty(refusedOutput.ToString());
        Assert.Equal(
            "Bootstrap is permanently unavailable after initialization.",
            refusedError.ToString().Trim());
        Assert.DoesNotContain(rawCapability, refusedError.ToString(), StringComparison.Ordinal);
    }

    private static async Task IssueAsync(
        ServiceProvider provider,
        string rawCapability)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        BootstrapCapabilityIssueResult result = await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(Hash(rawCapability));
        Assert.True(
            result is BootstrapCapabilityIssueResult.Created
                or BootstrapCapabilityIssueResult.Replaced);
    }

    private static async Task<InitialOwnerSetupResult> CreateOwnerInNewScopeAsync(
        ServiceProvider provider,
        string rawCapability,
        string userName)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<InitialOwnerSetupService>()
            .CreateAsync(
                new InitialOwnerSetupRequest(
                    rawCapability,
                    userName,
                    null,
                    ValidPassword));
    }

    private static byte[] Hash(string value)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static string CreateCapability(string seed)
    {
        return WebEncoders.Base64UrlEncode(Hash(seed));
    }

    private static CreatorToolkitDbContext GetStoreContext(object store)
    {
        for (Type? type = store.GetType(); type is not null; type = type.BaseType)
        {
            PropertyInfo? property = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(
                    candidate =>
                        candidate.GetIndexParameters().Length == 0
                        && candidate.PropertyType == typeof(CreatorToolkitDbContext));
            if (property?.GetValue(store) is CreatorToolkitDbContext propertyContext)
            {
                return propertyContext;
            }

            FieldInfo? field = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(
                    candidate => candidate.FieldType == typeof(CreatorToolkitDbContext));
            if (field?.GetValue(store) is CreatorToolkitDbContext fieldContext)
            {
                return fieldContext;
            }
        }

        throw new InvalidOperationException("The Identity store context could not be inspected.");
    }

    private static string CreateFailureTrigger(string step)
    {
        return step switch
        {
            "user" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE INSERT ON AspNetUsers
                BEGIN SELECT RAISE(ABORT, 'injected user failure'); END;
                """,
            "role" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE INSERT ON AspNetUserRoles
                BEGIN SELECT RAISE(ABORT, 'injected role failure'); END;
                """,
            "workspace" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE INSERT ON Workspaces
                BEGIN SELECT RAISE(ABORT, 'injected workspace failure'); END;
                """,
            "ownership" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE INSERT ON Ownerships
                BEGIN SELECT RAISE(ABORT, 'injected ownership failure'); END;
                """,
            "installation" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE UPDATE OF InitializedAtUtc ON InstallationStates
                BEGIN SELECT RAISE(ABORT, 'injected installation failure'); END;
                """,
            "capability" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE UPDATE OF UsedAtUtc ON SecurityCapabilities
                BEGIN SELECT RAISE(ABORT, 'injected capability failure'); END;
                """,
            "audit" => """
                CREATE TRIGGER fail_initial_owner_step
                BEFORE INSERT ON AuditRecords
                BEGIN SELECT RAISE(ABORT, 'injected audit failure'); END;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    private static async Task AssertNoPartialInitializationAsync(
        ServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitDbContext db = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.UserRoles.CountAsync());
        Assert.Equal(0, await db.Workspaces.CountAsync());
        Assert.Equal(0, await db.Ownerships.CountAsync());
        Assert.Null((await db.InstallationStates.SingleAsync()).InitializedAtUtc);
        SecurityCapability capability = await db.SecurityCapabilities.SingleAsync();
        Assert.Null(capability.UsedAtUtc);
        Assert.Null(capability.RevokedAtUtc);
        Assert.Equal(SecurityCapability.BootstrapOwnerActiveSlot, capability.ActiveSlot);
        Assert.Equal(
            0,
            await db.AuditRecords.CountAsync(
                record => record.EventCode == "identity.initial-owner-created"));
    }

    private static void AssertSecretAbsentFromDatabaseFiles(
        string dataDirectory,
        string secret)
    {
        foreach (string path in Directory.EnumerateFiles(
            dataDirectory,
            "creator-toolkit.db*",
            SearchOption.TopDirectoryOnly))
        {
            Assert.False(
                Encoding.Latin1
                    .GetString(File.ReadAllBytes(path))
                    .Contains(secret, StringComparison.Ordinal));
        }
    }

    private sealed class FailingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Synthetic audit failure.");
        }
    }
}
