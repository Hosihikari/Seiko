using System.Text;

namespace Seiko;

public static class ConfigWriter
{
    public static void WriteSpecs(string root, string section, IReadOnlyList<SeiSpec> specs)
    {
        if (section is not ("sei" or "components"))
        {
            throw new SeikoException($"unsupported config section: {section}", "config");
        }

        var lines = ReadLines(root);
        ReplaceSection(lines, section, BuildSpecsBlock(section, specs));
        WriteLines(root, lines);
    }

    public static void WriteSources(string root, IReadOnlyList<SourceConfig> sources)
    {
        var lines = ReadLines(root);
        ReplaceSection(lines, "sources", BuildSourcesBlock(sources));
        WriteLines(root, lines);
    }

    public static void WriteRid(string root, string? rid)
    {
        var lines = ReadLines(root);
        var index = FindScalar(lines, "rid");
        if (string.IsNullOrWhiteSpace(rid))
        {
            if (index >= 0)
            {
                lines.RemoveAt(index);
            }
        }
        else
        {
            var value = $"rid = \"{Escape(rid)}\"";
            if (index >= 0)
            {
                lines[index] = value;
            }
            else
            {
                var insertAt = FindSection(lines, "sources")?.Start
                    ?? FindSection(lines, "sei")?.Start
                    ?? lines.Count;
                lines.Insert(insertAt, value);
            }
        }

        WriteLines(root, lines);
    }

    private static List<string> ReadLines(string root)
    {
        var path = Path.Combine(root, "seiko.conf");
        if (!File.Exists(path))
        {
            throw new SeikoException($"missing {path}; run `sk init`", "config");
        }

        return File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
    }

    private static void WriteLines(string root, List<string> lines)
    {
        var path = Path.Combine(root, "seiko.conf");
        var output = string.Join("\n", lines);
        if (!output.EndsWith('\n'))
        {
            output += "\n";
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static void ReplaceSection(List<string> lines, string section, List<string> block)
    {
        var range = FindSection(lines, section);
        if (range is { } found)
        {
            lines.RemoveRange(found.Start, found.End - found.Start + 1);
            lines.InsertRange(found.Start, block);
            return;
        }

        var insertAt = FindSection(lines, "components")?.Start
            ?? FindSection(lines, "sei")?.Start
            ?? FindSection(lines, "sources")?.Start
            ?? lines.Count;
        if (insertAt == lines.Count && lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add("");
        }

        lines.InsertRange(insertAt, block);
    }

    private static List<string> BuildSpecsBlock(string section, IReadOnlyList<SeiSpec> specs)
    {
        if (specs.Count == 0)
        {
            return [$"{section} = []"];
        }

        var lines = new List<string> { $"{section} = [" };
        foreach (var spec in specs)
        {
            var name = Escape(spec.Name);
            if (string.IsNullOrWhiteSpace(spec.Version))
            {
                lines.Add($"  {{ name = \"{name}\" }},");
            }
            else
            {
                lines.Add($"  {{ name = \"{name}\", version = \"{Escape(spec.Version)}\" }},");
            }
        }

        lines.Add("]");
        return lines;
    }

    private static List<string> BuildSourcesBlock(IReadOnlyList<SourceConfig> sources)
    {
        if (sources.Count == 0)
        {
            return ["sources = []"];
        }

        var lines = new List<string> { "sources = [" };
        foreach (var source in sources)
        {
            var parts = new List<string>
            {
                $"id = \"{Escape(source.Id)}\"",
                $"index = \"{Escape(source.Index)}\"",
            };
            if (!string.IsNullOrWhiteSpace(source.TokenEnv))
            {
                parts.Add($"tokenEnv = \"{Escape(source.TokenEnv)}\"");
            }

            if (source.TokenHosts is { Count: > 0 } tokenHosts)
            {
                var hosts = tokenHosts.Select(host => $"\"{Escape(host)}\"");
                parts.Add($"tokenHosts = [{string.Join(", ", hosts)}]");
            }

            lines.Add($"  {{ {string.Join(", ", parts)} }},");
        }

        lines.Add("]");
        return lines;
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static int FindScalar(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed[key.Length..].TrimStart();
            if (rest.StartsWith('='))
            {
                return i;
            }
        }

        return -1;
    }

    private static (int Start, int End)? FindSection(List<string> lines, string section)
    {
        for (var line = 0; line < lines.Count; line++)
        {
            var trimmed = lines[line].TrimStart();
            if (!trimmed.StartsWith(section, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed[section.Length..].TrimStart();
            if (!rest.StartsWith('='))
            {
                continue;
            }

            var bracketIndex = lines[line].IndexOf('[', lines[line].IndexOf('=') + 1);
            if (bracketIndex < 0)
            {
                throw new SeikoException($"{section} must be an array", "config");
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var current = line; current < lines.Count; current++)
            {
                var start = current == line ? bracketIndex : 0;
                for (var i = start; i < lines[current].Length; i++)
                {
                    var ch = lines[current][i];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (ch == '\\')
                        {
                            escaped = true;
                        }
                        else if (ch == '"')
                        {
                            inString = false;
                        }

                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = true;
                    }
                    else if (ch == '#')
                    {
                        break;
                    }
                    else if (ch == '/' && i + 1 < lines[current].Length && lines[current][i + 1] == '/')
                    {
                        break;
                    }
                    else if (ch == '[')
                    {
                        depth++;
                    }
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return (line, current);
                        }
                    }
                }
            }

            throw new SeikoException($"{section} array is not closed", "config");
        }

        return null;
    }
}
