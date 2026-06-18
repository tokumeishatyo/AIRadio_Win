using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/pronunciations.yaml</c> のローダ（読み辞書 → <see cref="PronunciationEntry"/>。仕様 W19a §3）。
/// 空・未生成・ファイル無しは正常（空リストを返す＝同期なし）、ファイルが存在して壊れている
/// （パース不能 / surface 欠落 / pronunciation 欠落）場合のみ throw（fail-fast, <c>E-CFG-MISSING-FIELD-001</c>）。
/// 規約は <see cref="ArtistsConfig"/> に倣う。
/// </summary>
public static class PronunciationsConfig
{
    public static IReadOnlyList<PronunciationEntry> FromYaml(string yaml)
    {
        // 空文字・コメントのみ（出荷時の空ファイル）は正常＝空辞書。
        var meaningful = yaml.Split('\n').Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 0 && !trimmed.StartsWith('#');
        });
        if (!meaningful)
        {
            return Array.Empty<PronunciationEntry>();
        }

        Dto? dto;
        try
        {
            dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        }
        catch (Exception ex)
        {
            throw ConfigException.MissingField($"pronunciations.yaml を解釈できません: {ex.Message}");
        }

        var entries = dto?.Pronunciations;
        if (entries is null || entries.Count == 0)
        {
            return Array.Empty<PronunciationEntry>();   // `pronunciations:` 無し／空も正常（同期なし）。
        }

        var result = new List<PronunciationEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Surface))
            {
                throw ConfigException.MissingField("pronunciations[].surface");
            }
            if (string.IsNullOrEmpty(e.Pronunciation))
            {
                throw ConfigException.MissingField($"pronunciations[].pronunciation ({e.Surface})");
            }
            result.Add(new PronunciationEntry(
                e.Surface, e.Pronunciation, e.AccentType ?? 0, e.WordType, e.Priority));
        }
        return result;
    }

    /// <summary>ファイルが無いのは正常（出荷時に同梱しない運用も許容）＝空リスト。</summary>
    public static IReadOnlyList<PronunciationEntry> LoadFile(string path) =>
        File.Exists(path) ? FromYaml(File.ReadAllText(path)) : Array.Empty<PronunciationEntry>();

    public sealed class Dto
    {
        public List<EntryDto>? Pronunciations { get; set; }
    }

    public sealed class EntryDto
    {
        public string? Surface { get; set; }
        public string? Pronunciation { get; set; }
        public int? AccentType { get; set; }
        public string? WordType { get; set; }
        public int? Priority { get; set; }
    }
}
