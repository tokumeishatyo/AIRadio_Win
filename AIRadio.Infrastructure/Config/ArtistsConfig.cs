using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/artists.yaml</c> のローダ（アーティスト特集のプール → <see cref="ArtistProfile"/>。W15 §9-2）。
/// <b>出荷時は空</b>（メニューの生成ボタンで作成）。空・未生成・コメントのみは正常（空リストを返す）。
/// ファイルが存在して壊れている（パース不能 / name 欠落 / id 欠落 / id 重複 / 正規化 name 重複）場合のみ
/// throw（fail-fast、<c>E-CFG-MISSING-FIELD-001</c>）。<b>ゲスト（<see cref="GuestsConfig"/>）と異なり空は throw しない</b>（spec §18-3）。
/// reading は W15 では読まない（id/name のみ。W19b 送り＝§18-12。yaml にあっても <c>IgnoreUnmatchedProperties</c> で無視）。
/// </summary>
public static class ArtistsConfig
{
    public static IReadOnlyList<ArtistProfile> FromYaml(string yaml)
    {
        // 空文字・コメントのみ（出荷時の空ファイル）は正常＝空プール。
        var meaningful = yaml.Split('\n').Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 0 && !trimmed.StartsWith('#');
        });
        if (!meaningful)
        {
            return Array.Empty<ArtistProfile>();
        }

        Dto? dto;
        try
        {
            dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        }
        catch (Exception ex)
        {
            throw ConfigException.MissingField($"artists.yaml を解釈できません: {ex.Message}");
        }

        var artists = dto?.Artists;
        if (artists is null || artists.Count == 0)
        {
            return Array.Empty<ArtistProfile>();   // `artists:` 無し／空も正常（特集はスキップされる）。
        }

        var seenId = new HashSet<string>();
        var seenName = new HashSet<string>();
        var result = new List<ArtistProfile>(artists.Count);
        foreach (var a in artists)
        {
            if (string.IsNullOrEmpty(a.Id))
            {
                throw ConfigException.MissingField("artists[].id");
            }
            if (string.IsNullOrEmpty(a.Name))
            {
                throw ConfigException.MissingField($"artists[].name ({a.Id})");
            }
            if (!seenId.Add(a.Id))
            {
                throw ConfigException.MissingField($"artists[].id が重複: {a.Id}");
            }
            var canonical = CanonicalName(a.Name);
            if (canonical.Length > 0 && !seenName.Add(canonical))
            {
                throw ConfigException.MissingField($"artists[].name が重複: {a.Name}");
            }
            result.Add(new ArtistProfile(a.Id, a.Name));
        }
        return result;
    }

    /// <summary>ファイルが無いのは正常（出荷時は空＝未生成）＝空リスト。他ローダの素 <c>ReadAllText</c> と異なる契約（§18-3）。</summary>
    public static IReadOnlyList<ArtistProfile> LoadFile(string path) =>
        File.Exists(path) ? FromYaml(File.ReadAllText(path)) : Array.Empty<ArtistProfile>();

    /// <summary>重複判定用の正規化名（前後空白除去・小文字化・全角/半角スペース除去）。</summary>
    public static string CanonicalName(string name) =>
        name.Trim().ToLowerInvariant().Replace("　", "").Replace(" ", "");

    public sealed class Dto
    {
        public List<ArtistDto>? Artists { get; set; }
    }

    public sealed class ArtistDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }
}
