namespace Seiko;

// Lock files are intentionally never deleted. Closing and then deleting the
// path lets a second process open the old file while a third creates a new
// one, so both would hold a "lock" for the same name.
public sealed class ProcessLock : IDisposable
{
    private readonly FileStream _stream;

    private ProcessLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ProcessLock? TryAcquire(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new ProcessLock(
                new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ProcessLock AcquireWaiting(
        string path,
        string description,
        string code = "lock",
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        var delay = 25;
        while (true)
        {
            ProcessLock? acquired = null;
            try
            {
                acquired = new ProcessLock(
                    new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None));
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new SeikoException(
                    $"{description} cannot be locked: {ex.Message}",
                    "io",
                    ex);
            }
            catch (IOException ex) when (!IsContention(path, ex))
            {
                throw new SeikoException(
                    $"{description} cannot be locked: {ex.Message}",
                    "io",
                    ex);
            }
            catch (IOException)
            {
                // The file is in use; wait below.
            }

            if (acquired is not null)
            {
                return acquired;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new SeikoException($"{description} is in use by another process", code);
            }

            cancellationToken.WaitHandle.WaitOne(delay);
            cancellationToken.ThrowIfCancellationRequested();
            delay = Math.Min(delay * 2, 500);
        }
    }

    private static bool IsContention(string path, IOException error)
    {
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        if (error.HResult is sharingViolation or lockViolation)
        {
            return true;
        }

        try
        {
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // A read attempt is blocked as well. If the file exists, the
            // exclusive open was denied by sharing rather than by the path.
            return File.Exists(path);
        }
    }

    public static ProcessLock Acquire(string path, string description, string code = "lock")
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new ProcessLock(
                new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None));
        }
        catch (IOException ex)
        {
            throw new SeikoException($"{description} is in use by another process", code, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SeikoException($"{description} cannot be locked: {ex.Message}", "io", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
