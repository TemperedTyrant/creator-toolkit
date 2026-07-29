using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;

internal sealed class BestEffortDiagnosticRecorder(
    IServiceScopeFactory scopeFactory,
    IDiagnosticReferenceGenerator referenceGenerator,
    ILogger<BestEffortDiagnosticRecorder> logger) : IDiagnosticRecorder
{
    private const int MaximumReferenceAttempts = 3;

    private static readonly Action<ILogger, string, string, Exception?> LogRecorded =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(3100, "UnexpectedFailureRecorded"),
            "An unexpected failure was recorded. Reference: {DiagnosticReference}; "
            + "exception type: {ExceptionType}.");

    private static readonly Action<ILogger, string, Exception?> LogPersistenceFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3101, "DiagnosticPersistenceFailed"),
            "A diagnostic record could not be persisted. Reference: {DiagnosticReference}");

    private static readonly Action<ILogger, Exception?> LogReferenceGenerationFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3102, "DiagnosticReferenceGenerationFailed"),
            "A diagnostic reference could not be generated.");

    public async Task<DiagnosticReference> RecordAsync(
        UnexpectedDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        for (int attempt = 1; attempt <= MaximumReferenceAttempts; attempt++)
        {
            DiagnosticReference candidateReference;
            try
            {
                candidateReference = referenceGenerator.Create();
            }
            catch (Exception)
            {
                TryLogReferenceGenerationFailure(logger);
                return DiagnosticReference.CreateRandom();
            }

            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                DiagnosticPersistence persistence =
                    scope.ServiceProvider.GetRequiredService<DiagnosticPersistence>();

                DiagnosticReference persistedReference = await persistence.PersistAsync(
                    candidateReference,
                    diagnosticEvent,
                    cancellationToken);
                TryLogRecorded(
                    logger,
                    persistedReference.Value,
                    diagnosticEvent.ExceptionTypeCode);
                return persistedReference;
            }
            catch (DiagnosticReferenceCollisionException)
                when (attempt < MaximumReferenceAttempts)
            {
                continue;
            }
            catch (DiagnosticReferenceCollisionException)
            {
                DiagnosticReference fallbackReference = DiagnosticReference.CreateRandom();
                TryLogPersistenceFailure(logger, fallbackReference.Value);
                return fallbackReference;
            }
            catch (Exception)
            {
                // Do not attach the exception: exception messages and stack traces can contain paths.
                // Logging is deliberately not routed back through the diagnostic recorder.
                TryLogPersistenceFailure(logger, candidateReference.Value);
                return candidateReference;
            }
        }

        throw new InvalidOperationException("Diagnostic reference retry processing failed.");
    }

    private static void TryLogRecorded(
        ILogger logger,
        string reference,
        string exceptionType)
    {
        try
        {
            LogRecorded(logger, reference, exceptionType, null);
        }
        catch (Exception)
        {
            // A logging-provider failure cannot replace the original application failure.
        }
    }

    private static void TryLogPersistenceFailure(ILogger logger, string reference)
    {
        try
        {
            LogPersistenceFailure(logger, reference, null);
        }
        catch (Exception)
        {
            // A logging-provider failure cannot replace the original application failure.
        }
    }

    private static void TryLogReferenceGenerationFailure(ILogger logger)
    {
        try
        {
            LogReferenceGenerationFailure(logger, null);
        }
        catch (Exception)
        {
            // A logging-provider failure cannot replace the original application failure.
        }
    }
}
