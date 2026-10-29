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

        var path = Path.Combine(root, "seiko.conf");
        if (!File.Exists(path))
        {
            throw new SeikoException($"missing {path}; run `sk init`", "config");
        }

        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').ToList();
        var block = BuildBlock(section, specs);
        var range = FindSection(lines, section);
        if (range is { } found)
        {
            lines.RemoveRange(found.Start, found.End - found.Start + 1);
            lines.InsertRange(found.Start, block);
        }
        else
        {
            var components = FindSection(lines, "components");
            var insertAt = components?.Start ?? lines.Count;
            if (insertAt == lines.Count && lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }

            lines.InsertRange(insertAt, block);
        }

        var output = string.Join("\n", lines);
        if (!output.EndsWith('\n'))
        {
            output += "\n";
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static List<string> BuildBlock(string section, IReadOnlyList<SeiSpec> specs)
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

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

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

            var bracketLine = line;
            var bracketIndex = lines[line].IndexOf('[', lines[line].IndexOf('=') + 1);
            if (bracketIndex < 0)
            {
                throw new SeikoException($"{section} must be an array", "config");
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var current = bracketLine; current < lines.Count; current++)
            {
                var start = current == bracketLine ? bracketIndex : 0;
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
