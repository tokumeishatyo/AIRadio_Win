using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/program.yaml</c>（v1 = 明示 <c>segments:</c> 列）のローダ → <see cref="ProgramFormat"/>。
/// <c>anchor_dj_id</c> / <c>talk</c> の <c>corner_id</c> 欠落、未知の <c>type</c> は fail-fast
/// （<see cref="ConfigException"/>, <c>E-CFG-MISSING-FIELD-001</c>）。コーナー数 N 駆動の v2 部品宣言は W13 で導入し、
/// 本ローダ（v1）はその時点で置換する。
/// </summary>
public static class ProgramConfig
{
    public static ProgramFormat FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var program = dto?.Program ?? throw ConfigException.MissingField("program");

        if (string.IsNullOrEmpty(program.AnchorDjId))
        {
            throw ConfigException.MissingField("program.anchor_dj_id");
        }
        var segments = program.Segments;
        if (segments is null || segments.Count == 0)
        {
            throw ConfigException.MissingField("program.segments");
        }

        return new ProgramFormat(
            Title: program.Title ?? "ケイラボAIラジオ",
            AnchorDjId: program.AnchorDjId,
            Segments: segments.Select(Map).ToList());
    }

    public static ProgramFormat LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static ProgramSegment Map(SegmentDto s)
    {
        var kind = ParseKind(s.Type);
        string? cornerId = null;
        if (kind == SegmentKind.Talk)
        {
            if (string.IsNullOrEmpty(s.CornerId))
            {
                throw ConfigException.MissingField("program.segments[].corner_id（talk は必須）");
            }
            cornerId = s.CornerId;
        }
        return new ProgramSegment(kind, cornerId, s.Critical ?? false);
    }

    private static SegmentKind ParseKind(string? raw) => raw switch
    {
        "opening" => SegmentKind.Opening,
        "talk" => SegmentKind.Talk,
        "news" => SegmentKind.News,
        "ending" => SegmentKind.Ending,
        _ => throw ConfigException.MissingField($"program.segments[].type に未知の値: {raw}"),
    };

    public sealed class Dto
    {
        public ProgramDto? Program { get; set; }
    }

    public sealed class ProgramDto
    {
        public string? Title { get; set; }
        public string? AnchorDjId { get; set; }
        public List<SegmentDto>? Segments { get; set; }
    }

    public sealed class SegmentDto
    {
        public string? Type { get; set; }
        public string? CornerId { get; set; }
        public bool? Critical { get; set; }
    }
}
