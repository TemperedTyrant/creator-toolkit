namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    internal void Advance(TimeSpan duration)
    {
        _utcNow += duration;
    }
}
