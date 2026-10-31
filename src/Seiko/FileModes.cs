namespace Seiko;

internal static class FileModes
{
    // 0777: ordinary permission bits only. setuid, setgid, and sticky are
    // rejected because an install root may be operated by a privileged user.
    public const int AllowedMask = 0x1FF;

    public static bool TryParse(string text, out int value)
    {
        value = 0;
        try
        {
            value = Convert.ToInt32(text, 8);
            return value >= 0;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    public static bool HasSpecialBits(int value) => (value & ~AllowedMask) != 0;
}
