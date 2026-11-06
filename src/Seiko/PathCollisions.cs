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
    // so "dir/x" sits next to "dir" and not after "dir!". Comparison is per
    // code point: upper-casing UTF-16 units one at a time would not fold
    // supplementary-plane letters, which OrdinalIgnoreCase does fold, and the
    // equal pair could then be sorted apart.
    private static int Compare(Entry left, Entry right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Key.Length && rightIndex < right.Key.Length)
        {
            Rune.DecodeFromUtf16(left.Key.AsSpan(leftIndex), out var a, out var aLength);
            Rune.DecodeFromUtf16(right.Key.AsSpan(rightIndex), out var b, out var bLength);
            var leftOrder = Order(a);
            var rightOrder = Order(b);
            if (leftOrder != rightOrder)
            {
                return leftOrder < rightOrder ? -1 : 1;
            }

            leftIndex += aLength;
            rightIndex += bLength;
        }

        return (left.Key.Length - leftIndex).CompareTo(right.Key.Length - rightIndex);
    }

    private static int Order(Rune value)
        => value.Value == '/' ? 0 : Rune.ToUpperInvariant(value).Value;

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
