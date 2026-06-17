using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/program.yaml</c>（v2 = 部品宣言）のローダ → <see cref="ProgramBlueprint"/>（仕様 w13 §4）。
/// セグメント列は <see cref="ProgramPlan"/> がコーナー数 N から生成するため、本ローダは部品（OP/song/talk/letter/news）と
/// 既定の番組長だけを読む。<c>anchor_dj_id</c> / <c>song.fallback_track_uri</c> / <c>talk.corner_id</c> /
/// <c>letter.corner_id</c> 欠落、<c>default_length</c> 不正は fail-fast（<see cref="ConfigException"/>,
/// <c>E-CFG-MISSING-FIELD-001</c>）。曜日替わり編成（<c>weekly_cast</c>, W13.5）・ゲスト（<c>guest.corner_id</c>, W14）も読む。
/// アーティスト特集（<c>artist_feature</c>）は W15 で追加（現状は <c>IgnoreUnmatchedProperties</c> で無視）。
/// </summary>
public static class ProgramConfig
{
    public static ProgramBlueprint FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var program = dto?.Program ?? throw ConfigException.MissingField("program");

        if (string.IsNullOrEmpty(program.AnchorDjId))
        {
            throw ConfigException.MissingField("program.anchor_dj_id");
        }
        if (program.Song is not SongDto songDto || string.IsNullOrEmpty(songDto.FallbackTrackUri))
        {
            throw ConfigException.MissingField("program.song.fallback_track_uri");
        }
        if (string.IsNullOrEmpty(program.Talk?.CornerId))
        {
            throw ConfigException.MissingField("program.talk.corner_id");
        }
        if (string.IsNullOrEmpty(program.Letter?.CornerId))
        {
            throw ConfigException.MissingField("program.letter.corner_id");
        }

        return new ProgramBlueprint(
            Title: program.Title ?? "ケイラボAIラジオ",
            AnchorDjId: program.AnchorDjId,
            DefaultLength: ParseDefaultLength(program.DefaultLength),
            OpeningCritical: program.Opening?.Critical ?? true,
            Song: new SongSegmentSpec(
                FallbackTrackUri: SpotifyUri.NormalizeTrack(songDto.FallbackTrackUri),
                PromptHint: songDto.SongPromptHint ?? "",
                Volume: songDto.Volume ?? 100,
                PlaySeconds: songDto.PlaySeconds ?? 0),
            TalkCornerId: program.Talk.CornerId,
            LetterCornerId: program.Letter.CornerId,
            NewsDjId: program.News?.DjId,
            GuestCornerId: program.Guest?.CornerId)
        {
            WeeklyCast = ParseWeeklyCast(program.WeeklyCast),
        };
    }

    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays = new Dictionary<string, DayOfWeek>
    {
        ["sunday"] = DayOfWeek.Sunday,
        ["monday"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday,
    };

    /// <summary>
    /// <c>weekly_cast</c>（曜日名→順序付き DJ）を <see cref="WeeklyCast"/> に。省略（null/空）時は <see cref="WeeklyCast.Standard"/>。
    /// 不正な曜日名・空編成は fail-fast（Mac <c>parseWeeklyCast</c> 一致）。曜日名は小文字化して照合する。
    /// </summary>
    private static WeeklyCast ParseWeeklyCast(Dictionary<string, List<string>>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return WeeklyCast.Standard;
        }
        var casts = new Dictionary<DayOfWeek, IReadOnlyList<string>>();
        foreach (var (day, ids) in raw)
        {
            if (!Weekdays.TryGetValue(day.ToLowerInvariant(), out var weekday))
            {
                throw ConfigException.MissingField($"program.weekly_cast の曜日名が不正: {day}");
            }
            if (ids is null || ids.Count == 0)
            {
                throw ConfigException.MissingField($"program.weekly_cast.{day} の編成が空です");
            }
            casts[weekday] = ids;
        }
        return new WeeklyCast(casts);
    }

    public static ProgramBlueprint LoadFile(string path) => FromYaml(File.ReadAllText(path));

    /// <summary>
    /// <c>default_length</c> を解釈する。YamlDotNet はスカラノード（<c>10</c> / <c>"10"</c> / <c>endless</c> / <c>yes</c> 等）を
    /// すべて string プロパティへ束縛するため、不正判定は <see cref="ProgramLength.TryParse"/> に一本化する。
    /// **欠落（key 無し = null）のみ既定 <c>corners(10)</c>** に倒し、空文字・負数・小数・非数値は fail-fast（Mac 一致）。
    /// corners は 1 以上を要求（メニューに出さない 0 / endless は config 経由でのみ可だが、0 は不正）。
    /// </summary>
    private static ProgramLength ParseDefaultLength(string? raw)
    {
        if (raw is null)
        {
            return ProgramLength.FromCorners(10);
        }
        if (!ProgramLength.TryParse(raw, out var length))
        {
            throw ConfigException.MissingField($"program.default_length が不正（1 以上の整数または endless）: {raw}");
        }
        if (!length.IsEndless && length.Corners < 1)
        {
            throw ConfigException.MissingField($"program.default_length は 1 以上（または endless）: {raw}");
        }
        return length;
    }

    public sealed class Dto
    {
        public ProgramDto? Program { get; set; }
    }

    public sealed class ProgramDto
    {
        public string? Title { get; set; }
        public string? AnchorDjId { get; set; }
        public string? DefaultLength { get; set; }
        public OpeningDto? Opening { get; set; }
        public SongDto? Song { get; set; }
        public TalkDto? Talk { get; set; }
        public TalkDto? Letter { get; set; }
        public NewsDto? News { get; set; }
        public TalkDto? Guest { get; set; }   // W14: ゲストコーナー（corner_id のみ。artist_feature は W15 で未追加＝無視）
        public Dictionary<string, List<string>>? WeeklyCast { get; set; }
    }

    public sealed class OpeningDto
    {
        public bool? Critical { get; set; }
    }

    public sealed class SongDto
    {
        public string? SongPromptHint { get; set; }
        public string? FallbackTrackUri { get; set; }
        public int? Volume { get; set; }
        public int? PlaySeconds { get; set; }
    }

    public sealed class TalkDto
    {
        public string? CornerId { get; set; }
    }

    public sealed class NewsDto
    {
        public string? DjId { get; set; }
    }
}
