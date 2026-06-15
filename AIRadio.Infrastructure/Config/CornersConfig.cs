using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/corners.yaml</c> のローダ（コーナーテンプレート一覧 → <see cref="CornerTemplate"/>）。
/// W6 では基本フィールドのみ読む。テーマプール（themes）・時報リード文（lead_in）・アーティスト特集パラメータは
/// 後続スライスで追加（<c>IgnoreUnmatchedProperties</c> により現時点の YAML に在っても読み飛ばす）。
/// </summary>
public static class CornersConfig
{
    public static IReadOnlyList<CornerTemplate> FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var corners = dto?.Corners;
        if (corners is null || corners.Count == 0)
        {
            throw ConfigException.MissingField("corners");
        }
        return corners.Select(Map).ToList();
    }

    public static IReadOnlyList<CornerTemplate> LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static CornerTemplate Map(CornerDto c)
    {
        if (string.IsNullOrEmpty(c.Id))
        {
            throw ConfigException.MissingField("corners[].id");
        }
        if (string.IsNullOrEmpty(c.FallbackTrackUri))
        {
            throw ConfigException.MissingField($"corners[{c.Id}].fallback_track_uri");
        }
        return new CornerTemplate(
            Id: c.Id,
            Title: c.Title ?? c.Id,
            Theme: c.Theme ?? "",
            Format: ParseFormat(c.Format),
            DjIds: c.DjIds ?? new List<string>(),
            FallbackTrackUri: c.FallbackTrackUri,
            TargetMinutes: c.TargetMinutes ?? 5,
            CharsPerMinute: c.CharsPerMinute ?? 320,
            SongPromptHint: c.SongPromptHint ?? "",
            Volume: c.Volume ?? 85,
            PlaySeconds: c.PlaySeconds ?? 0);
    }

    private static CornerFormat ParseFormat(string? raw) => raw switch
    {
        null or "" or "free_talk" => CornerFormat.FreeTalk,
        "letter" => CornerFormat.Letter,
        "guest" => CornerFormat.Guest,
        "artist_feature" => CornerFormat.ArtistFeature,
        _ => throw ConfigException.MissingField($"未知の format: {raw}"),
    };

    public sealed class Dto
    {
        public List<CornerDto>? Corners { get; set; }
    }

    public sealed class CornerDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Theme { get; set; }
        public string? Format { get; set; }
        public List<string>? DjIds { get; set; }
        public int? TargetMinutes { get; set; }
        public int? CharsPerMinute { get; set; }
        public string? SongPromptHint { get; set; }
        public string? FallbackTrackUri { get; set; }
        public int? Volume { get; set; }
        public int? PlaySeconds { get; set; }
    }
}
