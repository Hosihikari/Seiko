using System.Security.Cryptography;

namespace Seiko;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static bool IsSha256(string? value)
        => value is { Length: 64 } && IsLowerHex(value);

    internal static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SeikoException("artifact digest is missing", "checksum");
        }

        var normalized = value.ToLowerInvariant();
        if (!IsSha256(normalized))
        {
            throw new SeikoException(
                $"artifact digest is not 64 lowercase hexadecimal characters: {value}",
                "checksum");
        }

        return normalized;
    }
}
