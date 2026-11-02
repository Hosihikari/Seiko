namespace Seiko;

// Deployed files may legitimately carry modes without the owner-read bit.
// Core operations that must inspect such a file temporarily add read access
// and restore the original mode in a finally block.
internal static class ReadableFile
{
    public static T WithReadAccess<T>(string path, Func<T> action)
    {
        var original = AddReadAccess(path);
        try
        {
            return action();
        }
        finally
        {
            Restore(path, original);
        }
    }

    public static void Copy(string source, string destination, bool overwrite)
        => WithReadAccess(source, () =>
        {
            File.Copy(source, destination, overwrite);
            return 0;
        });

    private static UnixFileMode? AddReadAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserRead) == 0)
        {
            File.SetUnixFileMode(path, mode | UnixFileMode.UserRead);
        }

        return mode;
    }

    private static void Restore(string path, UnixFileMode? mode)
    {
        if (mode is not { } original || OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
