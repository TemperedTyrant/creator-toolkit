namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed record DataDirectoryLayout(
    string Root,
    string DatabasePath,
    string KeyRingPath,
    string LockPath)
{
    public static DataDirectoryLayout Prepare(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        try
        {
            string root = Path.GetFullPath(dataDirectory);
            string keyRingPath = Path.Combine(root, "keys");
            string lockPath = Path.Combine(root, "locks");

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(keyRingPath);
            Directory.CreateDirectory(lockPath);

            SetRestrictiveDirectoryMode(root);
            SetRestrictiveDirectoryMode(keyRingPath);
            SetRestrictiveDirectoryMode(lockPath);

            return new DataDirectoryLayout(
                root,
                Path.Combine(root, "creator-toolkit.db"),
                keyRingPath,
                lockPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The configured data storage is unavailable.");
        }
    }

    private static void SetRestrictiveDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
