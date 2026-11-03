namespace Seiko;

public static class InitTemplate
{
    public const string OfficialSourceId = "official";
    public const string OfficialIndex =
        "https://raw.githubusercontent.com/Hosihikari/SeiIndex/main/index.json";

    public static string Write(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "seiko.conf");
        if (File.Exists(path))
        {
            throw new SeikoException($"{path} already exists", "io");
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Create() + "\n");
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
        }

        return path;
    }

    public static string Create() => $$"""
# Seiko install root
schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}

# The official index; remove it or add sources to use other seis.
sources = [
  { id = "{{OfficialSourceId}}", index = "{{OfficialIndex}}" }
]

# rid = "win-x64"
sei = []
""";
}
