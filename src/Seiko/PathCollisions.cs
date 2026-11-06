using System.Text;

namespace Seiko;

// Payload paths must be unique under the rules the deployment applies: case
// and Unicode normalization are folded, and no path may be both a file and a
// directory prefix of another (ADR-0024). The index verifier, the store and
// the external declarations share this so a package cannot pass one check and
// fail the next. Both directions are checked, because the order in which the
// two colliding paths are declared is irrelevant.
internal sealed class PathCollisions
{
    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    public IReadOnlyDictionary<string, string> Entries => _byKey;

    // Returns false with the already-added path and why they collide.
    public bool TryAdd(string path, out PathCollision? collision)
    {
        var key = Key(path);
        if (_byKey.TryGetValue(key, out var otherKey))
        {
            collision = new PathCollision(otherKey, PathCollisionKind.SamePath);
            return false;
        }

        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '/' && _byKey.TryGetValue(key[..i], out var filePrefix))
            {
                collision = new PathCollision(filePrefix, PathCollisionKind.FileBlocksDirectory);
                return false;
            }
        }

        var prefix = key + "/";
        foreach (var (candidate, original) in _byKey)
        {
            if (candidate.Length > prefix.Length
                && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                collision = new PathCollision(original, PathCollisionKind.DirectoryBlocksFile);
                return false;
            }
        }

        _byKey[key] = path;
        collision = null;
        return true;
    }
}

internal enum PathCollisionKind
{
    // Same path modulo case or Unicode normalization.
    SamePath,
    // The already-added path is a file where the new path needs a directory.
    FileBlocksDirectory,
    // The already-added path lives below the new path, which must be a file.
    DirectoryBlocksFile,
}

internal sealed record PathCollision(string Other, PathCollisionKind Kind);
