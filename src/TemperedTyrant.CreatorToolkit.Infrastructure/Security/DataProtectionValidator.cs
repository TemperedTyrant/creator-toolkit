using Microsoft.AspNetCore.DataProtection;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Security;

public interface IDataProtectionValidator
{
    bool IsUsable();

    Task<bool> IsUsableAsync(CancellationToken cancellationToken);
}

internal sealed class DataProtectionValidator : IDataProtectionValidator
{
    private const string Canary = "data-protection-startup-canary";
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private ValidationOperation? _inFlightOperation;

    public DataProtectionValidator(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(
            "TemperedTyrant.CreatorToolkit.StartupValidation.v1");
        _timeProvider = timeProvider;
    }

    public bool IsUsable() => ValidateCore();

    public async Task<bool> IsUsableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<bool> boundedResult = GetOrStartValidation();
        return await boundedResult.WaitAsync(cancellationToken);
    }

    private Task<bool> GetOrStartValidation()
    {
        lock (_sync)
        {
            if (_inFlightOperation is not null)
            {
                if (!_inFlightOperation.ValidationTask.IsCompleted)
                {
                    return _inFlightOperation.BoundedResult;
                }

                _inFlightOperation.Dispose();
                _inFlightOperation = null;
            }

            CancellationTokenSource timeoutSource =
                new(ValidationTimeout, _timeProvider);
            Task<bool> validationTask = Task.Run(ValidateCore, CancellationToken.None);
            ValidationOperation operation = new(timeoutSource, validationTask);
            operation.BoundedResult = AwaitBoundedResultAsync(operation);
            _inFlightOperation = operation;
            _ = validationTask.ContinueWith(
                static (completed, state) =>
                {
                    var completion = ((DataProtectionValidator Validator,
                        ValidationOperation Operation))state!;
                    completion.Validator.ClearCompletedValidation(
                        completion.Operation,
                        completed);
                },
                (this, operation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return operation.BoundedResult;
        }
    }

    private bool ValidateCore()
    {
        try
        {
            string protectedCanary = _protector.Protect(Canary);
            return string.Equals(
                _protector.Unprotect(protectedCanary),
                Canary,
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> AwaitBoundedResultAsync(
        ValidationOperation operation)
    {
        try
        {
            return await operation.ValidationTask.WaitAsync(
                operation.TimeoutSource.Token);
        }
        catch (OperationCanceledException)
            when (operation.TimeoutSource.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ClearCompletedValidation(
        ValidationOperation operation,
        Task<bool> completedValidation)
    {
        _ = completedValidation.Exception;
        lock (_sync)
        {
            if (ReferenceEquals(_inFlightOperation, operation))
            {
                _inFlightOperation = null;
            }
        }

        operation.Dispose();
    }

    private sealed class ValidationOperation(
        CancellationTokenSource timeoutSource,
        Task<bool> validationTask)
    {
        private int _disposed;

        internal CancellationTokenSource TimeoutSource { get; } = timeoutSource;

        internal Task<bool> ValidationTask { get; } = validationTask;

        internal Task<bool> BoundedResult { get; set; } = null!;

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                TimeoutSource.Dispose();
            }
        }
    }
}
