using System.Text;

namespace Seiko;

// Payload paths must be unique under the rules the deployment applies: case
// and Unicode normalization are folded, and no path may be both a file and a
// directory prefix of another (ADR-0024). The index verifier, the store and
// the external declarations share this so a package cannot pass one check and
// fail the next. Both directions are checked, because the order in which the
// two colliding paths are declared is irrelevant.
//
// A package may carry 100,000 entries, so the reverse direction (an existing
// descendant below the new path) cannot scan every added path. Every add
// records its ancestor directories with one example descendant instead; the
// check then costs the path depth.
internal sealed class PathCollisions
{
    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _descendants = new(StringComparer.OrdinalIgnoreCase);

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
            if (key[i] != '/')
            {
                continue;
            }

            if (_byKey.TryGetValue(key[..i], out var filePrefix))
            {
                collision = new PathCollision(filePrefix, PathCollisionKind.FileBlocksDirectory);
                return false;
            }
        }

        if (_descendants.TryGetValue(key, out var descendant))
        {
            collision = new PathCollision(descendant, PathCollisionKind.DirectoryBlocksFile);
            return false;
        }

        _byKey[key] = path;
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '/')
            {
                _descendants.TryAdd(key[..i], path);
            }
        }

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
