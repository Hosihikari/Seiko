using System.Runtime.InteropServices;

namespace Seiko;

internal enum LinkKind
{
    Reflink,
    Hardlink,
    Copy,
}

// Deploys a store file without copying its bytes when the filesystem allows it.
// A reflink keeps the deployed file fully independent; a hardlink shares the
// inode with the store, so editing the deployed file in place also edits the
// store and is rejected by the recorded digests on the next deployment.
internal static class FileLink
{
    private const nuint FicLone = 0x40049409;

    public static LinkKind Create(string source, string destination)
    {
        if (TryReflink(source, destination))
        {
            return LinkKind.Reflink;
        }

        if (TryHardLink(source, destination))
        {
            return LinkKind.Hardlink;
        }

        File.Copy(source, destination, overwrite: true);
        return LinkKind.Copy;
    }

    private static bool TryReflink(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (CloneFile(source, destination, 0) != 0)
                {
                    TryDelete(destination);
                    return false;
                }

                CopyMode(source, destination);
                return true;
            }

            if (!OperatingSystem.IsLinux())
            {
                return false;
            }

            bool cloned;
            using (var sourceHandle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destinationHandle = File.OpenHandle(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                cloned = Ioctl(
                    destinationHandle.DangerousGetHandle().ToInt32(),
                    FicLone,
                    sourceHandle.DangerousGetHandle().ToInt32()) == 0;
            }

            if (!cloned)
            {
                TryDelete(destination);
                return false;
            }

            CopyMode(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or DllNotFoundException
            or EntryPointNotFoundException)
        {
            TryDelete(destination);
            return false;
        }
    }

    private static bool TryHardLink(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLink(destination, source, IntPtr.Zero);
            }

            return Link(source, destination) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void CopyMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // DllImport keeps the variadic-friendly signatures simple and works under
    // NativeAOT on every supported platform.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int Link(string oldpath, string newpath);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int Ioctl(int fd, nuint request, int arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "clonefile")]
    private static extern int CloneFile(string source, string destination, uint flags);
#pragma warning restore SYSLIB1054
}
