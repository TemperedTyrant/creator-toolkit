namespace TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;

internal sealed class FileProcessLock(string path, TimeProvider timeProvider)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public async Task<FileProcessLockLease> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long startedAt = timeProvider.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileStream stream = new(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                SetRestrictiveFileMode(path);
                return new FileProcessLockLease(stream);
            }
            catch (IOException) when (timeProvider.GetElapsedTime(startedAt) < timeout)
            {
                await Task.Delay(RetryDelay, timeProvider, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new TimeoutException(
                    "The operation could not acquire its process-coordination lock.");
            }
        }
    }

    public bool IsHeld()
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            SetRestrictiveFileMode(path);
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void SetRestrictiveFileMode(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

internal sealed class FileProcessLockLease(FileStream stream) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        return stream.DisposeAsync();
    }
}
