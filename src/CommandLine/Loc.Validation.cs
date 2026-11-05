namespace Seiko.CommandLine;

// ADR-0034: every key exists in every language and every form keeps the
// English placeholders. The test project calls this; keeping the check next
// to the catalogs makes a missing translation a structural failure rather
// than a silent English fallback.
internal static partial class Loc
{
    private static readonly string[] Languages = ["en", "zh-Hans", "fr", "ru", "es", "ar"];

    internal static IReadOnlyList<string> ValidateCatalogs()
    {
        var problems = new List<string>();
        var reference = Merge("en");
        if (reference.Count == 0)
        {
            problems.Add("en: catalog is empty");
            return problems;
        }

        if (!TextCatalogs.ContainsKey("en"))
        {
            problems.Add("en: text catalog is missing");
        }

        foreach (var language in Languages.Skip(1))
        {
            var catalog = Merge(language);
            foreach (var (key, forms) in reference)
            {
                if (!catalog.TryGetValue(key, out var localized))
                {
                    problems.Add($"{language}: missing {key}");
                    continue;
                }

                var expected = Placeholders(forms[0]);
                foreach (var form in localized)
                {
                    if (!expected.SetEquals(Placeholders(form)))
                    {
                        problems.Add($"{language}: placeholders differ in {key}");
                    }
                }
            }

            foreach (var key in catalog.Keys.Except(reference.Keys, StringComparer.Ordinal))
            {
                problems.Add($"{language}: extra {key}");
            }
        }

        return problems;
    }

    private static Dictionary<string, string[]> Merge(string language)
    {
        var merged = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var source in new[] { Catalogs, TextCatalogs })
        {
            if (source.TryGetValue(language, out var catalog))
            {
                foreach (var (key, forms) in catalog)
                {
                    merged[key] = forms;
                }
            }
        }

        return merged;
    }

    private static HashSet<int> Placeholders(string form)
    {
        var indexes = new HashSet<int>();
        for (var i = 0; i < form.Length; i++)
        {
            if (form[i] != '{')
            {
                continue;
            }

            var end = form.IndexOf('}', i + 1);
            if (end < 0)
            {
                break;
            }

            if (int.TryParse(form.AsSpan(i + 1, end - i - 1), out var index))
            {
                indexes.Add(index);
            }

            i = end;
        }

        return indexes;
    }
}
