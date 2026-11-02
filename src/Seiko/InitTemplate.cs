namespace Seiko;

public static class InitTemplate
{
    public const string OfficialSourceId = "official";
    public const string OfficialIndex =
        "https://raw.githubusercontent.com/Hosihikari/SeiIndex/main/index.json";

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
