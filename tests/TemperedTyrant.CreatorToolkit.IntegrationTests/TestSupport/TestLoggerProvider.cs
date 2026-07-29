using Microsoft.Extensions.Logging;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class TestLoggerProvider(ICollection<string> messages) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new TestLogger(messages);
    }

    public void Dispose()
    {
    }

    private sealed class TestLogger(ICollection<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
