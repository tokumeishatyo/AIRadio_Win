using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/themes.yaml</c> のローダ → <see cref="BroadcastThemes"/>（OP / news / ED の BGM 演出 + 固定口上
/// + 時間帯挨拶 <c>greetings:</c>, W8）。W4 <see cref="ThemeSequencer"/> がローダを消費者（= W7 <see cref="BroadcastEngine"/>）へ
/// 委譲したもの。W7 は anchor が読む単一・フラット形（曜日替わり DJ の <c>by_dj</c> は W13.5）。news の announcement は
/// 実行時のニュース原稿で差し替えるため読まない。<c>track_uri</c> 欠落は fail-fast（<c>E-CFG-MISSING-FIELD-001</c>）。
/// <c>greetings:</c> はブロックごと・または各キーが欠落（null）したときのみ既定の挨拶に倒す（Mac 一致。明示的な空文字はそのまま）。
/// </summary>
public static class ThemesConfig
{
    public static BroadcastThemes FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var opening = dto?.Opening ?? throw ConfigException.MissingField("themes.opening");
        var news = dto?.News ?? throw ConfigException.MissingField("themes.news");
        var ending = dto?.Ending ?? throw ConfigException.MissingField("themes.ending");

        return new BroadcastThemes(
            Opening: MapTheme(opening, "opening"),
            OpeningAnnouncement: opening.Announcement ?? "",
            News: MapTheme(news, "news"),
            Ending: MapTheme(ending, "ending"),
            EndingAnnouncement: ending.Announcement ?? "",
            Greetings: MapGreetings(dto?.Greetings));
    }

    public static BroadcastThemes LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static Greetings MapGreetings(GreetingsDto? g)
    {
        var fallback = new Greetings();
        if (g is null)
        {
            return fallback;
        }
        // キー欠落（null）のみ既定に倒す（Mac の `file.greetings?.x ?? defaults.x` と一致）。
        // 明示的な空文字はそのまま採用する（Mac 完全一致）。
        return new Greetings(
            Morning: g.Morning ?? fallback.Morning,
            Afternoon: g.Afternoon ?? fallback.Afternoon,
            Evening: g.Evening ?? fallback.Evening);
    }

    private static ThemeConfig MapTheme(ThemeDto t, string name)
    {
        if (string.IsNullOrEmpty(t.TrackUri))
        {
            throw ConfigException.MissingField($"themes.{name}.track_uri");
        }
        return new ThemeConfig(
            Tagline: t.Tagline, // ED は null（いきなり BGM）
            TrackUri: SpotifyUri.NormalizeTrack(t.TrackUri),
            IntroSeconds: t.IntroSeconds ?? 5,
            Volume: t.Volume ?? 100,
            DuckedVolume: t.DuckedVolume ?? 35,
            OutroSeconds: t.OutroSeconds ?? 10);
    }

    public sealed class Dto
    {
        public ThemeDto? Opening { get; set; }
        public ThemeDto? News { get; set; }
        public ThemeDto? Ending { get; set; }
        public GreetingsDto? Greetings { get; set; }
    }

    public sealed class GreetingsDto
    {
        public string? Morning { get; set; }
        public string? Afternoon { get; set; }
        public string? Evening { get; set; }
    }

    public sealed class ThemeDto
    {
        public string? Tagline { get; set; }
        public string? TrackUri { get; set; }
        public int? IntroSeconds { get; set; }
        public int? Volume { get; set; }
        public int? DuckedVolume { get; set; }
        public int? OutroSeconds { get; set; }
        public string? Announcement { get; set; }
    }
}
