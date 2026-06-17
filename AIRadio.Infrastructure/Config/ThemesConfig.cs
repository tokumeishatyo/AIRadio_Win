using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/themes.yaml</c> のローダ → <see cref="BroadcastThemes"/>。OP / ED は DJ 別固定口上（<c>by_dj</c> →
/// <see cref="ThemedSegment"/>, W13.5）、news は単一（<see cref="ThemeConfig"/>。読み手は龍星固定。announcement は
/// 実行時のニュース原稿で差し替えるため読まない）。+ 時間帯挨拶 <c>greetings:</c>（W8）。
/// <c>track_uri</c> 欠落・OP/ED の <c>by_dj</c> 欠落・各 spiel の <c>announcement</c> 欠落は fail-fast
/// （<c>E-CFG-MISSING-FIELD-001</c>、Mac <c>ThemeConfigLoader</c> 一致）。
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
            Opening: BuildThemed(opening, "opening"),
            News: BuildSingle(news, "news"),
            Ending: BuildThemed(ending, "ending"),
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
        // キー欠落（null）のみ既定に倒す（Mac の `file.greetings?.x ?? defaults.x` と一致）。明示的な空文字はそのまま。
        return new Greetings(
            Morning: g.Morning ?? fallback.Morning,
            Afternoon: g.Afternoon ?? fallback.Afternoon,
            Evening: g.Evening ?? fallback.Evening);
    }

    /// <summary>共有 BGM 演出。<c>tagline</c> は per-DJ のため staging では持たない（news のみ後で載せ直す）。</summary>
    private static ThemeConfig Staging(ThemeDto t, string name)
    {
        if (string.IsNullOrEmpty(t.TrackUri))
        {
            throw ConfigException.MissingField($"themes.{name}.track_uri");
        }
        return new ThemeConfig(
            Tagline: null,
            TrackUri: SpotifyUri.NormalizeTrack(t.TrackUri),
            IntroSeconds: t.IntroSeconds ?? 5,
            Volume: t.Volume ?? 100,
            DuckedVolume: t.DuckedVolume ?? 35,
            OutroSeconds: t.OutroSeconds ?? 10);
    }

    /// <summary>OP / ED: 共有演出 + DJ 別固定口上。<c>by_dj</c> 欠落・各 <c>announcement</c> 欠落は fail-fast。</summary>
    private static ThemedSegment BuildThemed(ThemeDto t, string name)
    {
        var staging = Staging(t, name);
        if (t.ByDj is null || t.ByDj.Count == 0)
        {
            throw ConfigException.MissingField($"themes.{name}.by_dj");
        }
        var byDj = new Dictionary<string, DjSpiel>();
        foreach (var (id, spiel) in t.ByDj)
        {
            if (string.IsNullOrEmpty(spiel?.Announcement))
            {
                throw ConfigException.MissingField($"themes.{name}.by_dj.{id}.announcement");
            }
            byDj[id] = new DjSpiel(spiel.Announcement, spiel.Tagline);
        }
        return new ThemedSegment(staging, byDj);
    }

    /// <summary>news: 単一の読み手（tagline 保持。announcement は実行時注入のため読まない）。</summary>
    private static ThemeConfig BuildSingle(ThemeDto t, string name) => Staging(t, name) with { Tagline = t.Tagline };

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
        public string? Tagline { get; set; }       // news 用（単一）。OP/ED は by_dj 側に持つ。
        public string? TrackUri { get; set; }
        public int? IntroSeconds { get; set; }
        public int? Volume { get; set; }
        public int? DuckedVolume { get; set; }
        public int? OutroSeconds { get; set; }
        public Dictionary<string, SpielDto>? ByDj { get; set; }  // OP/ED の DJ 別固定口上。
    }

    public sealed class SpielDto
    {
        public string? Tagline { get; set; }
        public string? Announcement { get; set; }
    }
}
