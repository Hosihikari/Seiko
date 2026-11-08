using Hocon;
using Hocon.Extensions.Configuration;
using Microsoft.Extensions.Configuration;

namespace Seiko;

public static class ConfigLoader
{
    public static SeikoConfig Load(string root, bool requireSources = true)
        => LoadPath(Path.Combine(root, "seiko.conf"), requireSources);

    internal static SeikoConfig LoadPath(string path, bool requireSources = true)
    {
        if (!File.Exists(path))
        {
            throw new SeikoException($"missing {path}; run `sk init` or pass --root", "config");
        }

        IConfiguration configuration;
        try
        {
            configuration = new ConfigurationBuilder()
                .AddHoconFile(path, optional: false)
                .Build();
        }
        catch (Exception ex)
        {
            throw new SeikoException($"{path}: {ex.Message}", "config", ex);
        }

        var schemaText = configuration["schemaVersion"];
        if (!int.TryParse(schemaText, out var schemaVersion)
            || schemaVersion != SeikoConfig.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"{path}: unsupported config schemaVersion '{schemaText ?? "(missing)"}'",
                "config");
        }

        var hasComponents = configuration.GetSection("components").Exists()
            || configuration.GetSection("components").GetChildren().Any();
        if (!hasComponents)
        {
            try
            {
                hasComponents = HoconParser.Parse(File.ReadAllText(path), null)
                    .TryGetValue("components", out _);
            }
            catch (Exception ex)
            {
                throw new SeikoException($"{path}: {ex.Message}", "config", ex);
            }
        }

        if (hasComponents)
        {
            throw new SeikoException(
                $"{path}: components is not supported; declare direct dependencies in sei",
                "config");
        }

        var config = new SeikoConfig
        {
            Rid = configuration["rid"],
        };

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection("sources").GetChildren())
        {
            var id = child["id"];
            var index = child["index"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(index))
            {
                throw new SeikoException($"{path}: each source needs id and index", "config");
            }

            if (!sourceIds.Add(id))
            {
                throw new SeikoException($"{path}: duplicate source id '{id}'", "config");
            }

            var tokenHosts = child.GetSection("tokenHosts")
                .GetChildren()
                .Select(section => section.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            config.Sources.Add(new SourceConfig
            {
                Id = id,
                Index = index,
                TokenHosts = tokenHosts.Count > 0 ? tokenHosts : null,
            });
        }

        if (requireSources && config.Sources.Count == 0)
        {
            throw new SeikoException($"{path}: at least one source is required", "config");
        }

        ReadSpecs(configuration.GetSection("sei"), config.Sei, path, "sei");
        ReadOrigins(configuration.GetSection("origin"), config, path);
        EnsureUniqueDirect(config, path);
        return config;
    }

    // `origin` is a reserved source id: a configured source of that name would
    // make the lock's `source = "origin"` ambiguous (ADR-0044).
    private static void EnsureUniqueDirect(SeikoConfig config, string path)
    {
        if (config.Sources.Any(source => string.Equals(source.Id, OriginPaths.SourceId, StringComparison.Ordinal)))
        {
            throw new SeikoException(
                $"{path}: source id '{OriginPaths.SourceId}' is reserved for local files",
                "config");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in config.Sei)
        {
            if (!names.Add(spec.Name))
            {
                throw new SeikoException($"{path}: duplicate direct declaration '{spec.Name}'", "config");
            }
        }

        // An origin entry pairs with the sei entry of the same name: `sei`
        // declares the direct set, `origin` only pins where the bytes come
        // from, so the name appears in both on purpose (ADR-0044).
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in config.Origin)
        {
            if (!origins.Add(origin.Name))
            {
                throw new SeikoException($"{path}: duplicate origin entry '{origin.Name}'", "config");
            }

            if (!names.Contains(origin.Name))
            {
                throw new SeikoException(
                    $"{path}: origin '{origin.Name}' has no matching sei declaration",
                    "config");
            }
        }
    }

    private static void ReadOrigins(IConfigurationSection section, SeikoConfig config, string path)
    {
        foreach (var child in section.GetChildren())
        {
            var name = child["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SeikoException($"{path}: each origin entry needs a name", "config");
            }

            var paths = child.GetSection("paths").GetChildren()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();
            if (paths.Count == 0)
            {
                throw new SeikoException($"{path}: origin '{name}' needs at least one path", "config");
            }

            var selectors = child.GetSection("selectors").GetChildren()
                .Select(ReadSelector)
                .ToList();
            if (selectors.Count == 0)
            {
                selectors.Add(new OriginSelector());
            }

            // Older writers could leave the arrays ragged; a missing selector
            // means "any condition", which is what the first one defaults to.
            while (selectors.Count < paths.Count)
            {
                selectors.Add(new OriginSelector());
            }

            if (selectors.Count != paths.Count)
            {
                throw new SeikoException(
                    $"{path}: origin '{name}' has {paths.Count} path(s) but {selectors.Count} selector(s)",
                    "config");
            }

            config.Origin.Add(new OriginSpec
            {
                Name = name,
                Paths = paths,
                Selectors = selectors,
            });
        }
    }

    private static OriginSelector ReadSelector(IConfigurationSection section)
    {
        var prerequisites = section.GetSection("prerequisites").GetChildren()
            .Select(item => new SeiDependency
            {
                Name = item["name"] ?? "",
                Version = item["version"],
            })
            .ToList();
        return new OriginSelector
        {
            Rid = section["rid"],
            Prerequisites = prerequisites,
        };
    }

    private static void ReadSpecs(
        IConfigurationSection section,
        List<SeiSpec> specs,
        string path,
        string sectionName)
    {
        foreach (var child in section.GetChildren())
        {
            var name = child["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SeikoException($"{path}: each {sectionName} entry needs a name", "config");
            }

            specs.Add(new SeiSpec
            {
                Name = name,
                Version = child["version"],
            });
        }
    }
}
