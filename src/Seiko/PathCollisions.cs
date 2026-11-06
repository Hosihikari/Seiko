using System.Text;

namespace Seiko;

// Payload paths must be unique under the rules the deployment applies: case
// and Unicode normalization are folded, and no path may be both a file and a
// directory prefix of another (ADR-0024). The index verifier, the store and
// the external declarations share this so a package cannot pass one check and
// fail the next.
internal sealed class PathCollisions
{
    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    public IReadOnlyDictionary<string, string> Entries => _byKey;

    public bool TryAdd(string path, out string? other)
    {
        var key = Key(path);
        if (_byKey.TryGetValue(key, out other))
        {
            return false;
        }

        _byKey[key] = path;
        other = null;
        return true;
    }

    // An already-added path that is a directory prefix of this one.
    public string? FilePrefixOf(string path)
    {
        var key = Key(path);
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '/' && _byKey.TryGetValue(key[..i], out var file))
            {
                return file;
            }
        }

        return null;
    }

    // An already-added path that lies below this one.
    public string? DirectoryOfFile(string path)
    {
        var key = Key(path) + "/";
        foreach (var (otherKey, original) in _byKey)
        {
            if (otherKey.Length > key.Length
                && otherKey.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }
        }

        return null;
    }
}
