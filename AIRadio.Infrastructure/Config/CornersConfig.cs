using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/corners.yaml</c> のローダ（コーナーテンプレート一覧 → <see cref="CornerTemplate"/>）。
/// テーマプール（<c>themes</c>, W12）を読み込む。時報リード文（lead_in, W13.5）・アーティスト特集パラメータ（W15）は
/// 後続スライスで追加（<c>IgnoreUnmatchedProperties</c> により現時点の YAML に在っても読み飛ばす）。
/// id/title/theme/dj_ids/fallback_track_uri は必須（欠落は <c>E-CFG-MISSING-FIELD-001</c> で fail-fast。Mac 一致, §4-3）。
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
        // 起動時設定不正は fail-fast（§4-3）。Mac CornersConfigLoader と同じく id/title/theme/dj_ids/fallback を必須検証。
        if (string.IsNullOrEmpty(c.Id))
        {
            throw ConfigException.MissingField("corners[].id");
        }
        if (string.IsNullOrEmpty(c.Title))
        {
            throw ConfigException.MissingField($"corners[{c.Id}].title");
        }
        if (string.IsNullOrEmpty(c.Theme))
        {
            throw ConfigException.MissingField($"corners[{c.Id}].theme");
        }
        if (c.DjIds is null || c.DjIds.Count == 0)
        {
            throw ConfigException.MissingField($"corners[{c.Id}].dj_ids");
        }
        if (string.IsNullOrEmpty(c.FallbackTrackUri))
        {
            throw ConfigException.MissingField($"corners[{c.Id}].fallback_track_uri");
        }
        return new CornerTemplate(
            Id: c.Id,
            Title: c.Title,
            Theme: c.Theme,
            Format: ParseFormat(c.Format),
            DjIds: c.DjIds,
            FallbackTrackUri: c.FallbackTrackUri,
            TargetMinutes: c.TargetMinutes ?? 5,
            CharsPerMinute: c.CharsPerMinute ?? 320,
            SongPromptHint: c.SongPromptHint ?? "",
            Volume: c.Volume ?? 85,
            PlaySeconds: c.PlaySeconds ?? 0,
            ThemePool: c.Themes);
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
        public List<string>? Themes { get; set; }
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
