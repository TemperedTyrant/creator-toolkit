namespace TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;

internal static class DiagnosticRetention
{
    internal const int MaximumRecords = 1_000;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(1);
}
