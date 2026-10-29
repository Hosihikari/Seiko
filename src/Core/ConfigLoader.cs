using Hocon.Extensions.Configuration;
using Microsoft.Extensions.Configuration;

namespace Seiko.Core;

public static class ConfigLoader
{
    public static SeikoConfig Load(string root)
    {
        var path = Path.Combine(root, "seiko.conf");
        if (!File.Exists(path))
        {
            throw new SeikoException($"missing {path}; run `sk init` or pass --root");
        }

        var configuration = new ConfigurationBuilder()
            .AddHoconFile(path, optional: false)
            .Build();

        var config = new SeikoConfig
        {
            Rid = configuration["rid"],
        };

        foreach (var child in configuration.GetSection("sources").GetChildren())
        {
            var id = child["id"];
            var index = child["index"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(index))
            {
                throw new SeikoException($"{path}: each source needs id and index");
            }

            config.Sources.Add(new SourceConfig
            {
                Id = id,
                Index = index,
                Priority = int.TryParse(child["priority"], out var priority) ? priority : 0,
                TokenEnv = child["tokenEnv"],
            });
        }

        if (config.Sources.Count == 0)
        {
            throw new SeikoException($"{path}: at least one source is required");
        }

        ReadSpecs(configuration.GetSection("packages"), config.Packages);
        ReadSpecs(configuration.GetSection("components"), config.Components);
        return config;
    }

    private static void ReadSpecs(IConfigurationSection section, List<PackageSpec> specs)
    {
        foreach (var child in section.GetChildren())
        {
            var name = child["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            specs.Add(new PackageSpec
            {
                Name = name,
                Version = child["version"],
            });
        }
    }
}
