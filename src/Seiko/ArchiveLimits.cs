namespace Seiko;

internal static class ArchiveLimits
{
    public const int MaxEntries = 100_000;
    public const long MaxEntryBytes = 1L << 30;
    public const long MaxTotalBytes = 4L << 30;

    public static void EnsureEntryCount(int count)
    {
        if (count > MaxEntries)
        {
            throw new SeikoException(
                $"artifact has {count} entries; the limit is {MaxEntries}",
                "checksum");
        }
    }

    public static void EnsureEntrySize(long size)
    {
        if (size > MaxEntryBytes)
        {
            throw new SeikoException(
                $"artifact entry expands to {size} bytes; the limit is {MaxEntryBytes}",
                "checksum");
        }
    }

    public static string NormalizeName(string entryName)
    {
        var raw = entryName.Replace('\\', '/');
        if (raw.EndsWith("/", StringComparison.Ordinal))
        {
            return "";
        }

        return string.Join(
            '/',
            raw.Split('/').Where(segment => segment is not ("" or ".")));
    }

    public static void EnsureTotal(long total)
    {
        if (total > MaxTotalBytes)
        {
            throw new SeikoException(
                $"artifact expands beyond {MaxTotalBytes} bytes",
                "checksum");
        }
    }
}
