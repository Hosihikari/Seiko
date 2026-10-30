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
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
