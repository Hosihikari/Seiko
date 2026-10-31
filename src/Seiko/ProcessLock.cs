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
