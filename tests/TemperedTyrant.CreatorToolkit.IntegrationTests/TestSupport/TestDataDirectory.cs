namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class TestDataDirectory : IDisposable
{
    public TestDataDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "creator-toolkit-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
