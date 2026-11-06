using System.Text;

namespace Seiko;

// Payload paths must be unique under the rules the deployment applies: case
// and Unicode normalization are folded, and no path may be both a file and a
// directory prefix of another (ADR-0024). The index verifier, the store and
// the external declarations share this so a package cannot pass one check and
// fail the next.
//
// Callers collect every path, then validate once. The check sorts by the
// normalized key with the separator ordered below every other character, so a
// file and its descendant are always adjacent; this keeps only one string per
// path instead of one per ancestor, which used to grow with the square of the
// path depth.
internal sealed class PathCollisions
{
    private readonly List<Entry> _items = [];

    public static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    public void Add(string path) => _items.Add(new Entry(Key(path), path, _items.Count));

    // Collisions in the order the later participant was added; the record's
    // Other is always the file side.
    public IReadOnlyList<PathCollision> Validate()
    {
        if (_items.Count < 2)
        {
            return [];
        }

        var sorted = _items.ToArray();
        Array.Sort(sorted, Compare);
        var found = new List<(int Later, PathCollision Collision)>();
        for (var i = 0; i + 1 < sorted.Length; i++)
        {
            var left = sorted[i];
            var right = sorted[i + 1];
            if (string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase))
            {
                var (first, second) = left.Index <= right.Index ? (left, right) : (right, left);
                found.Add((second.Index, new PathCollision(first.Path, PathCollisionKind.SamePath)));
                continue;
            }

            if (right.Key.Length > left.Key.Length
                && right.Key[left.Key.Length] == '/'
                && right.Key.AsSpan(0, left.Key.Length).Equals(left.Key.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                // left is the shorter key, so it is always the file side.
                found.Add((Math.Max(left.Index, right.Index), new PathCollision(left.Path, PathCollisionKind.Prefix)));
            }
        }

        return found
            .OrderBy(item => item.Later)
            .Select(item => item.Collision)
            .ToList();
    }

    // Ordinal ignore-case, except that '/' sorts below every other character
    // so "dir/x" sits next to "dir" and not after "dir!".
    private static int Compare(Entry left, Entry right)
    {
        var length = Math.Min(left.Key.Length, right.Key.Length);
        for (var i = 0; i < length; i++)
        {
            var a = Order(left.Key[i]);
            var b = Order(right.Key[i]);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return left.Key.Length.CompareTo(right.Key.Length);
    }

    private static char Order(char value)
        => value == '/' ? '\0' : char.ToUpperInvariant(value);

    private readonly record struct Entry(string Key, string Path, int Index);
}

internal enum PathCollisionKind
{
    // Same path modulo case or Unicode normalization.
    SamePath,
    // One path is a file where the other needs a directory.
    Prefix,
}

internal sealed record PathCollision(string Other, PathCollisionKind Kind);
