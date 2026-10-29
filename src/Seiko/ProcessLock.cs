namespace Seiko;

public sealed class ProcessLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    public bool DeleteOnDispose { get; set; }

    private ProcessLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
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
                    FileShare.None),
                path);
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

    public void Dispose()
    {
        _stream.Dispose();
        if (!DeleteOnDispose)
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
