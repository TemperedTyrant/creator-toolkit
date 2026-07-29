using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.ErrorHandling;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Infrastructure;

public sealed partial class DiagnosticRecorderTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    private static readonly Action<ILogger, string, Exception?> LogFrameworkRequest =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(9001, "TestFrameworkRequest"),
            "Request data: {RequestData}");

    [Fact]
    public async Task DiagnosticPersistenceUsesIndependentScopeAndCommit()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);

        await using AsyncServiceScope callerScope = provider.CreateAsyncScope();
        CreatorToolkitDbContext callerContext =
            callerScope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        await using var callerTransaction =
            await callerContext.Database.BeginTransactionAsync();
        await callerTransaction.RollbackAsync();

        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        DiagnosticReference reference = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.Infrastructure,
                DiagnosticOperation.PersistenceInitialization));

        Assert.Empty(callerContext.ChangeTracker.Entries<DiagnosticRecord>());

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        DiagnosticRecord persisted = await verification.DiagnosticRecords.SingleAsync();
        Assert.Equal(reference.Value, persisted.Reference);
        Assert.Equal(FixedTime, persisted.OccurredAtUtc);
    }

    [Fact]
    public async Task DiagnosticTimestampIsNormalizedToUtcFromInjectedTimeProvider()
    {
        using TestDataDirectory data = new();
        DateTimeOffset configuredTime =
            new(2026, 7, 28, 20, 0, 0, TimeSpan.FromHours(5));
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: new ManualTimeProvider(configuredTime));
        await TestServices.InitializeAsync(provider);

        await provider.GetRequiredService<IDiagnosticRecorder>().RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.Infrastructure,
                DiagnosticOperation.PersistenceInitialization,
                DiagnosticExceptionType.Database));

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        DiagnosticRecord record = await verification.DiagnosticRecords.SingleAsync();
        Assert.Equal(configuredTime.ToUniversalTime(), record.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, record.OccurredAtUtc.Offset);
    }

    [Fact]
    public async Task RecorderFailureReturnsOpaqueReferenceWithoutRecursionOrExceptionDetails()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider =
            TestServices.Create(
                data.Path,
                logs,
                new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using (CreatorToolkitDbContext context =
                     await contextFactory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE DiagnosticRecords;");
        }
        logs.Clear();

        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        const string originalFailureMarker = "original-failure-marker-248c";
        UnexpectedFailureMiddleware middleware = new(
            _ => throw new InvalidOperationException(originalFailureMarker));
        DefaultHttpContext httpContext = CreateHttpContext();
        await middleware.InvokeAsync(httpContext, recorder);

        httpContext.Response.Body.Position = 0;
        using StreamReader reader = new(httpContext.Response.Body);
        string response = await reader.ReadToEndAsync();
        Match firstReference = OpaqueReferencePattern().Match(response);
        DiagnosticReference second = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.UnhandledRequest,
                DiagnosticOperation.HttpRequest));

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        Assert.True(firstReference.Success);
        Assert.DoesNotContain(originalFailureMarker, response, StringComparison.Ordinal);
        Assert.Matches(OpaqueReferencePattern(), second.Value);
        Assert.NotEqual(firstReference.Value, second.Value);
        Assert.Equal(
            2,
            logs.Count(message => message.Contains(
                "A diagnostic record could not be persisted",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(
            logs,
            message => message.Contains(data.Path, StringComparison.Ordinal)
                || message.Contains("Sqlite", StringComparison.Ordinal)
                || message.Contains("DiagnosticRecords", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task ExpectedClientOutcomesDoNotPersistDiagnostics(int statusCode)
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);

        SafeStatusCodeMiddleware middleware = new(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            });
        DefaultHttpContext httpContext = CreateHttpContext();

        await middleware.InvokeAsync(httpContext);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Empty(await verification.DiagnosticRecords.ToListAsync());
        Assert.Equal(statusCode, httpContext.Response.StatusCode);
        Assert.NotEqual(0, httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task UnexpectedRequestFailureReturnsOnlySanitizedOpaqueReference()
    {
        using TestDataDirectory data = new();
        const string secretMarker = "unexpected-secret-marker-731d";
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);

        UnexpectedFailureMiddleware middleware = new(
            _ => throw new InvalidOperationException(secretMarker));
        DefaultHttpContext httpContext = CreateHttpContext();
        httpContext.TraceIdentifier = "request-correlation-marker";
        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();

        await middleware.InvokeAsync(httpContext, recorder);

        httpContext.Response.Body.Position = 0;
        using StreamReader reader = new(httpContext.Response.Body);
        string response = await reader.ReadToEndAsync();
        Match referenceMatch = OpaqueReferencePattern().Match(response);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        Assert.True(referenceMatch.Success);
        Assert.DoesNotContain(secretMarker, response, StringComparison.Ordinal);
        Assert.DoesNotContain("request-correlation-marker", response, StringComparison.Ordinal);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        DiagnosticRecord diagnostic = await verification.DiagnosticRecords.SingleAsync();
        Assert.Equal(referenceMatch.Value, diagnostic.Reference);
        Assert.Equal(FixedTime, diagnostic.OccurredAtUtc);
        Assert.Equal("internal", diagnostic.Category);
        Assert.Equal("unhandled-request", diagnostic.ErrorCode);
        Assert.Equal("http-request", diagnostic.Operation);
        Assert.Equal("invalid-operation", diagnostic.ExceptionType);
    }

    [Fact]
    public async Task ExpectedBadRequestExceptionDoesNotPersistDiagnostic()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        UnexpectedFailureMiddleware middleware = new(
            _ => throw new BadHttpRequestException(
                "parser detail that must not be returned",
                StatusCodes.Status400BadRequest));
        DefaultHttpContext httpContext = CreateHttpContext();

        await middleware.InvokeAsync(
            httpContext,
            provider.GetRequiredService<IDiagnosticRecorder>());

        httpContext.Response.Body.Position = 0;
        using StreamReader reader = new(httpContext.Response.Body);
        string response = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.DoesNotContain("parser detail", response, StringComparison.Ordinal);

        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Empty(await verification.DiagnosticRecords.ToListAsync());
    }

    [Fact]
    public async Task RecorderContractViolationCannotReplaceOriginalSafeFailureResponse()
    {
        const string originalFailureMarker = "original-failure-marker-971e";
        UnexpectedFailureMiddleware middleware = new(
            _ => throw new InvalidOperationException(originalFailureMarker));
        DefaultHttpContext httpContext = CreateHttpContext();

        await middleware.InvokeAsync(httpContext, new ThrowingDiagnosticRecorder());

        httpContext.Response.Body.Position = 0;
        using StreamReader reader = new(httpContext.Response.Body);
        string response = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        Assert.Matches(OpaqueReferencePattern(), response);
        Assert.DoesNotContain(
            "CTK-00000000000000000000000000000000",
            response,
            StringComparison.Ordinal);
        Assert.DoesNotContain(originalFailureMarker, response, StringComparison.Ordinal);
        Assert.DoesNotContain("recorder failure", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsOlderThanThirtyDaysArePrunedWithBoundedWork()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(FixedTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();

        await using (CreatorToolkitDbContext seed = await contextFactory.CreateDbContextAsync())
        {
            IEnumerable<DiagnosticRecord> expired = Enumerable.Range(0, 1_000)
                .Select(index => DiagnosticRecord.Create(
                    ReferenceFor(index),
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.Infrastructure,
                        DiagnosticOperation.PersistenceInitialization),
                    FixedTime - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(index + 1)));
            seed.DiagnosticRecords.AddRange(expired);
            await seed.SaveChangesAsync();
        }

        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        DiagnosticReference retained = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.UnhandledRequest,
                DiagnosticOperation.HttpRequest));

        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        DiagnosticRecord onlyRecord = await verification.DiagnosticRecords.SingleAsync();
        Assert.Equal(retained.Value, onlyRecord.Reference);
        Assert.Equal(FixedTime, onlyRecord.OccurredAtUtc);
    }

    [Fact]
    public async Task DiagnosticStoreNeverExceedsOneThousandRows()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(FixedTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();

        await using (CreatorToolkitDbContext seed = await contextFactory.CreateDbContextAsync())
        {
            IEnumerable<DiagnosticRecord> records = Enumerable.Range(0, 1_000)
                .Select(index => DiagnosticRecord.Create(
                    ReferenceFor(index),
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.Infrastructure,
                        DiagnosticOperation.PersistenceInitialization),
                    FixedTime - TimeSpan.FromMinutes(20) + TimeSpan.FromMilliseconds(index)));
            seed.DiagnosticRecords.AddRange(records);
            await seed.SaveChangesAsync();
        }

        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        DiagnosticReference newest = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.UnhandledRequest,
                DiagnosticOperation.HttpRequest));

        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Equal(1_000, await verification.DiagnosticRecords.CountAsync());
        Assert.False(await verification.DiagnosticRecords.AnyAsync(
            record => record.Reference == ReferenceFor(0).Value));
        Assert.True(await verification.DiagnosticRecords.AnyAsync(
            record => record.Reference == newest.Value));
    }

    [Fact]
    public async Task RepeatedIdenticalFailuresAreDeduplicatedWithinBoundedWindow()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(FixedTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);
        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        UnexpectedDiagnosticEvent diagnosticEvent = new(
            DiagnosticFailureKind.Infrastructure,
            DiagnosticOperation.PersistenceInitialization,
            DiagnosticExceptionType.Database);

        DiagnosticReference first = await recorder.RecordAsync(diagnosticEvent);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        DiagnosticReference duplicate = await recorder.RecordAsync(diagnosticEvent);

        Assert.Equal(first, duplicate);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Single(await verification.DiagnosticRecords.ToListAsync());
    }

    [Fact]
    public async Task GenericUnhandledFailuresAreNotDeduplicatedTooBroadly()
    {
        using TestDataDirectory data = new();
        ManualTimeProvider timeProvider = new(FixedTime);
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: timeProvider);
        await TestServices.InitializeAsync(provider);
        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        UnexpectedDiagnosticEvent diagnosticEvent = new(
            DiagnosticFailureKind.UnhandledRequest,
            DiagnosticOperation.HttpRequest,
            DiagnosticExceptionType.InvalidOperation);

        DiagnosticReference first = await recorder.RecordAsync(diagnosticEvent);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        DiagnosticReference second = await recorder.RecordAsync(diagnosticEvent);

        Assert.NotEqual(first, second);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Equal(2, await verification.DiagnosticRecords.CountAsync());
    }

    [Fact]
    public async Task FixedDeduplicationKeyIncludesSanitizedExceptionType()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider =
            TestServices.Create(data.Path, timeProvider: new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);
        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();

        DiagnosticReference database = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.Infrastructure,
                DiagnosticOperation.PersistenceInitialization,
                DiagnosticExceptionType.Database));
        DiagnosticReference inputOutput = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.Infrastructure,
                DiagnosticOperation.PersistenceInitialization,
                DiagnosticExceptionType.InputOutput));

        Assert.NotEqual(database, inputOutput);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Equal(2, await verification.DiagnosticRecords.CountAsync());
    }

    [Fact]
    public async Task DiagnosticReferenceCollisionReceivesBoundedFreshReference()
    {
        using TestDataDirectory data = new();
        DiagnosticReference collision = ReferenceFor(8_000);
        DiagnosticReference replacement = ReferenceFor(8_001);
        QueueDiagnosticReferenceGenerator generator = new(collision, replacement);
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: new ManualTimeProvider(FixedTime),
            diagnosticReferenceGenerator: generator);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();

        await using (CreatorToolkitDbContext seed = await contextFactory.CreateDbContextAsync())
        {
            seed.DiagnosticRecords.Add(
                DiagnosticRecord.Create(
                    collision,
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.Infrastructure,
                        DiagnosticOperation.PersistenceInitialization,
                        DiagnosticExceptionType.Database),
                    FixedTime - TimeSpan.FromMinutes(10)));
            await seed.SaveChangesAsync();
        }

        IDiagnosticRecorder recorder = provider.GetRequiredService<IDiagnosticRecorder>();
        DiagnosticReference actual = await recorder.RecordAsync(
            new UnexpectedDiagnosticEvent(
                DiagnosticFailureKind.UnhandledRequest,
                DiagnosticOperation.HttpRequest,
                DiagnosticExceptionType.InvalidOperation));

        Assert.Equal(replacement, actual);
        Assert.Equal(2, generator.Calls);
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Equal(2, await verification.DiagnosticRecords.CountAsync());
        Assert.True(await verification.DiagnosticRecords.AnyAsync(
            record => record.Reference == replacement.Value));
    }

    [Fact]
    public async Task RepeatedReferenceCollisionsNeverReturnUnrelatedExistingReference()
    {
        using TestDataDirectory data = new();
        DiagnosticReference collision = ReferenceFor(8_100);
        QueueDiagnosticReferenceGenerator generator =
            new(collision, collision, collision);
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: new ManualTimeProvider(FixedTime),
            diagnosticReferenceGenerator: generator);
        await TestServices.InitializeAsync(provider);
        IDbContextFactory<CreatorToolkitDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<CreatorToolkitDbContext>>();

        await using (CreatorToolkitDbContext seed = await contextFactory.CreateDbContextAsync())
        {
            seed.DiagnosticRecords.Add(
                DiagnosticRecord.Create(
                    collision,
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.Infrastructure,
                        DiagnosticOperation.PersistenceInitialization,
                        DiagnosticExceptionType.Database),
                    FixedTime - TimeSpan.FromMinutes(10)));
            await seed.SaveChangesAsync();
        }

        DiagnosticReference actual = await provider
            .GetRequiredService<IDiagnosticRecorder>()
            .RecordAsync(
                new UnexpectedDiagnosticEvent(
                    DiagnosticFailureKind.UnhandledRequest,
                    DiagnosticOperation.HttpRequest,
                    DiagnosticExceptionType.InvalidOperation));

        Assert.Equal(3, generator.Calls);
        Assert.NotEqual(collision, actual);
        Assert.Matches(OpaqueReferencePattern(), actual.Value);
        await using CreatorToolkitDbContext verification =
            await contextFactory.CreateDbContextAsync();
        Assert.Single(await verification.DiagnosticRecords.ToListAsync());
    }

    [Fact]
    public async Task ReferenceGeneratorFailureReturnsFreshOpaqueFallbackWithoutMasking()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            timeProvider: new ManualTimeProvider(FixedTime),
            diagnosticReferenceGenerator: new ThrowingDiagnosticReferenceGenerator());
        await TestServices.InitializeAsync(provider);

        DiagnosticReference reference = await provider
            .GetRequiredService<IDiagnosticRecorder>()
            .RecordAsync(
                new UnexpectedDiagnosticEvent(
                    DiagnosticFailureKind.UnhandledRequest,
                    DiagnosticOperation.HttpRequest,
                    DiagnosticExceptionType.Unexpected));

        Assert.Matches(OpaqueReferencePattern(), reference.Value);
        Assert.DoesNotContain(
            "CTK-00000000000000000000000000000000",
            reference.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoggingAllowlistDropsRequestDataAndKeepsSanitizedDiagnosticEvent()
    {
        using TestDataDirectory data = new();
        List<string> logs = [];
        await using ServiceProvider provider = TestServices.Create(
            data.Path,
            logs,
            new ManualTimeProvider(FixedTime));
        await TestServices.InitializeAsync(provider);
        logs.Clear();
        const string requestMarker = "submitted-query-marker-466b";

        ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        string[] unsafeFrameworkCategories =
        [
            "Microsoft.AspNetCore.DataProtection",
            "Microsoft.AspNetCore.Hosting.Diagnostics",
            "Microsoft.EntityFrameworkCore.Database.Command",
            "Microsoft.EntityFrameworkCore.Database.Connection",
            "Microsoft.EntityFrameworkCore.Query",
            "Microsoft.EntityFrameworkCore.Update",
            "System.Net.Http.HttpClient",
        ];
        foreach (string category in unsafeFrameworkCategories)
        {
            ILogger frameworkLogger = loggerFactory.CreateLogger(category);
            LogFrameworkRequest(
                frameworkLogger,
                requestMarker,
                new InvalidOperationException(requestMarker));
        }
        DiagnosticReference reference = await provider
            .GetRequiredService<IDiagnosticRecorder>()
            .RecordAsync(
                new UnexpectedDiagnosticEvent(
                    DiagnosticFailureKind.Infrastructure,
                    DiagnosticOperation.PersistenceInitialization,
                    DiagnosticExceptionType.Database));

        Assert.DoesNotContain(
            logs,
            message => message.Contains(requestMarker, StringComparison.Ordinal));
        Assert.Contains(
            logs,
            message => message.Contains(reference.Value, StringComparison.Ordinal)
                && message.Contains("database", StringComparison.Ordinal));
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        return new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static DiagnosticReference ReferenceFor(int value)
    {
        return new DiagnosticReference($"CTK-{value:X32}");
    }

    [GeneratedRegex(@"CTK-[A-F0-9]{32}", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueReferencePattern();

    private sealed class ThrowingDiagnosticRecorder : IDiagnosticRecorder
    {
        public Task<DiagnosticReference> RecordAsync(
            UnexpectedDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("recorder failure");
        }
    }

    private sealed class QueueDiagnosticReferenceGenerator(
        params DiagnosticReference[] references) : IDiagnosticReferenceGenerator
    {
        private readonly Queue<DiagnosticReference> _references = new(references);

        internal int Calls { get; private set; }

        public DiagnosticReference Create()
        {
            Calls++;
            return _references.Dequeue();
        }
    }

    private sealed class ThrowingDiagnosticReferenceGenerator
        : IDiagnosticReferenceGenerator
    {
        public DiagnosticReference Create()
        {
            throw new InvalidOperationException("generator failure");
        }
    }
}
