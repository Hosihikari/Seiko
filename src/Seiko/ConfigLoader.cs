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
        return config;
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
