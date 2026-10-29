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
}
