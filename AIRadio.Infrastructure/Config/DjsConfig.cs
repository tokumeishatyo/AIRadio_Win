using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary><c>config/djs.yaml</c> のローダ（DJ プロフィール一覧 → <see cref="DjProfile"/>）。</summary>
public static class DjsConfig
{
    public static IReadOnlyList<DjProfile> FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var djs = dto?.Djs;
        if (djs is null || djs.Count == 0)
        {
            throw ConfigException.MissingField("djs");
        }
        return djs.Select(Map).ToList();
    }

    public static IReadOnlyList<DjProfile> LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static DjProfile Map(DjDto d)
    {
        if (string.IsNullOrEmpty(d.Id))
        {
            throw ConfigException.MissingField("djs[].id");
        }
        if (string.IsNullOrEmpty(d.Name))
        {
            throw ConfigException.MissingField($"djs[{d.Id}].name");
        }
        if (d.SpeakerId is null)
        {
            throw ConfigException.MissingField($"djs[{d.Id}].speaker_id");
        }
        if (string.IsNullOrEmpty(d.Persona))
        {
            throw ConfigException.MissingField($"djs[{d.Id}].persona");
        }
        return new DjProfile(d.Id, d.Name, d.SpeakerId.Value, d.Persona);
    }

    public sealed class Dto
    {
        public List<DjDto>? Djs { get; set; }
    }

    public sealed class DjDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? SpeakerId { get; set; }
        public string? Persona { get; set; }
    }
}
