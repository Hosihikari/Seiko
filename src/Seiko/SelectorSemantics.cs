namespace Seiko;

// Artifact selection treats "rid" keys and values case-insensitively and any
// other selector as a carrier name looked up in the resolved graph. Index
// verification uses the same equivalence so it cannot accept two artifacts
// that the resolver would then reject as ambiguous.
internal static class SelectorSemantics
{
    public static bool EquivalentForResolution(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
    {
        var a = Signature(left);
        var b = Signature(right);
        return a.Rid.SequenceEqual(b.Rid, StringComparer.OrdinalIgnoreCase)
            && a.Carrier.SequenceEqual(b.Carrier, StringComparer.Ordinal);
    }

    private static (List<string> Rid, List<string> Carrier) Signature(
        Dictionary<string, string>? selectors)
    {
        var rid = new List<string>();
        var carrier = new List<string>();
        foreach (var (key, value) in selectors ?? [])
        {
            if (key.Equals("rid", StringComparison.OrdinalIgnoreCase))
            {
                rid.Add(value);
            }
            else
            {
                carrier.Add(value);
            }
        }

        rid.Sort(StringComparer.OrdinalIgnoreCase);
        carrier.Sort(StringComparer.Ordinal);
        return (rid, carrier);
    }
}
