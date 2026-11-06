using System.Text;

namespace Seiko;

// Payload paths must be unique under the rules the deployment applies: case
// and Unicode normalization are folded, and no path may be both a file and a
// directory prefix of another (ADR-0024). The index verifier, the store and
// the external declarations share this so a package cannot pass one check and
// fail the next.
//
// Every path is kept once in a list; a dictionary of normalized keys to the
// first index answers both equality and ancestor lookups, so the rules can
// never disagree with each other. Ancestors are found through a span lookup,
// which slices without allocating, so no ancestor string is retained (the
// former per-ancestor keys grew with the square of the path depth).
internal sealed class PathCollisions
{
    private readonly Dictionary<string, int> _firstByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _items = [];

    public static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    public void Add(string path)
    {
        var key = Key(path);
        _firstByKey.TryAdd(key, _items.Count);
        _items.Add(new Entry(key, path));
    }

    // Collisions in the order the later participant was added; the record's
    // Other is always the file side. Additions are complete by now, so an
    // ancestor may have been added after its descendant; both directions are
    // reported once, keyed by the later index.
    public IReadOnlyList<PathCollision> Validate()
    {
        var found = new List<(int Later, PathCollision Collision)>();
        var lookup = _firstByKey.GetAlternateLookup<ReadOnlySpan<char>>();
        var reported = new HashSet<(int, int, PathCollisionKind)>();
        for (var index = 0; index < _items.Count; index++)
        {
            var entry = _items[index];

            // Same path modulo case or normalization, reported against the
            // earliest occurrence. Repeats keep participating below.
            var first = _firstByKey[entry.Key];
            if (first != index && reported.Add((first, index, PathCollisionKind.SamePath)))
            {
                found.Add((
                    Math.Max(first, index),
                    new PathCollision(_items[first].Path, PathCollisionKind.SamePath)));
            }

            for (var i = 0; i < entry.Key.Length; i++)
            {
                if (entry.Key[i] == '/'
                    && lookup.TryGetValue(entry.Key.AsSpan(0, i), out var file)
                    && file != index
                    && reported.Add((file, index, PathCollisionKind.Prefix)))
                {
                    found.Add((
                        Math.Max(file, index),
                        new PathCollision(_items[file].Path, PathCollisionKind.Prefix)));
                }
            }
        }

        return found
            .OrderBy(item => item.Later)
            .Select(item => item.Collision)
            .ToList();
    }

    private readonly record struct Entry(string Key, string Path);
}

internal enum PathCollisionKind
{
    // Same path modulo case or Unicode normalization.
    SamePath,
    // One path is a file where the other needs a directory.
    Prefix,
}

internal sealed record PathCollision(string Other, PathCollisionKind Kind);
