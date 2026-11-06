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
//
// A path that is itself a file for many descendants collides with each of
// them, so materializing every pair grows with entries times depth. Callers
// only need one error to reject, so the search stops at the first hit.
internal sealed class PathCollisions
{
    private readonly Dictionary<string, int> _firstByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _items = [];
    private string[]? _sorted;

    public static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    public void Add(string path)
    {
        var key = Key(path);
        _firstByKey.TryAdd(key, _items.Count);
        _items.Add(new Entry(key, path));
        _sorted = null;
    }

    // The first collision among the collected paths. The record's Other is
    // always the file side. Every path is already added, so an ancestor is
    // found whether or not it was declared before its descendant.
    public bool TryFind(out PathCollision? collision)
    {
        var lookup = _firstByKey.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var index = 0; index < _items.Count; index++)
        {
            var entry = _items[index];

            // Same path modulo case or normalization, against the earliest
            // occurrence. Repeats keep participating below.
            var first = _firstByKey[entry.Key];
            if (first != index)
            {
                collision = new PathCollision(_items[first].Path, PathCollisionKind.SamePath);
                return true;
            }

            if (FindBlockingAncestor(entry.Key, index, lookup) is { } file)
            {
                collision = new PathCollision(_items[file].Path, PathCollisionKind.Prefix);
                return true;
            }
        }

        collision = null;
        return false;
    }

    // Whether one more path would collide with the collected set, without
    // adding it. Used for external targets against the archive's own names.
    // Both directions are checked: an existing file blocking the target, and
    // the target being a file where an existing path needs a directory.
    public bool TryFindCollisionWith(string path, out PathCollision? collision)
    {
        var key = Key(path);
        if (_firstByKey.TryGetValue(key, out var first))
        {
            collision = new PathCollision(_items[first].Path, PathCollisionKind.SamePath);
            return true;
        }

        var lookup = _firstByKey.GetAlternateLookup<ReadOnlySpan<char>>();
        if (FindBlockingAncestor(key, -1, lookup) is { } file)
        {
            collision = new PathCollision(_items[file].Path, PathCollisionKind.Prefix);
            return true;
        }

        // Descendants: the sorted copy makes this a binary search instead of
        // a scan per target. It is built once and reused while no path is
        // added, which is the case for the whole target loop.
        if (_sorted is null)
        {
            _sorted = _items.Select(item => item.Key).ToArray();
            Array.Sort(_sorted, StringComparer.OrdinalIgnoreCase);
        }

        var sorted = _sorted;
        var prefix = key + "/";
        var at = Array.BinarySearch(sorted, prefix, StringComparer.OrdinalIgnoreCase);
        if (at < 0)
        {
            at = ~at;
        }

        if (at < sorted.Length
            && sorted[at].Length > prefix.Length
            && sorted[at].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            collision = new PathCollision(_items[_firstByKey[sorted[at]]].Path, PathCollisionKind.Prefix);
            return true;
        }

        collision = null;
        return false;
    }

    // Only the collected paths are in the dictionary, so the first hit is the
    // path being checked itself when it was already added; skip that one.
    private static int? FindBlockingAncestor(
        string key,
        int self,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup)
    {
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '/'
                && lookup.TryGetValue(key.AsSpan(0, i), out var file)
                && file != self)
            {
                return file;
            }
        }

        return null;
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
