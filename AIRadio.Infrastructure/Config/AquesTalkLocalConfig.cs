namespace AIRadio.Infrastructure;

/// <summary>
/// AquesTalk1 / AqKanji2Koe のライセンスキー（機密・<c>config/aquestalk.local.yaml</c>）。W-AQT。
/// キーはコミット対象ファイルに書かない（<c>*.local.yaml</c> は gitignore・CLAUDE.md §3-3）。
/// <para>
/// ローダは tolerant: ファイル不在・空・コメントのみ・キー欠落はすべて <see cref="Empty"/>（空文字）へ倒す（throw しない）。
/// dev key 未設定は評価版動作（ナ/マ行→ヌ）になるが合成自体は成功するため、composition 側で WARN ログを出す（spec §5-3）。
/// </para>
/// </summary>
public sealed record AquesTalkLocalConfig(
    string AquesTalkDevKey, string AquesTalkUsrKey, string AqKanji2KoeDevKey)
{
    /// <summary>全キー空（ファイル不在・未設定時）。</summary>
    public static readonly AquesTalkLocalConfig Empty = new("", "", "");

    /// <summary>YAML 文字列から構築（欠落・空はすべて空文字。throw しない）。</summary>
    public static AquesTalkLocalConfig FromYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty;
        }
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        if (dto is null)
        {
            return Empty;
        }
        return new AquesTalkLocalConfig(
            dto.AquestalkDevKey ?? "",
            dto.AquestalkUsrKey ?? "",
            dto.Aqkanji2koeDevKey ?? "");
    }

    /// <summary>ファイルから読み込む。ファイル不在は <see cref="Empty"/>（BYO・gitignore のため出荷時は無い）。</summary>
    public static AquesTalkLocalConfig LoadFile(string path)
        => File.Exists(path) ? FromYaml(File.ReadAllText(path)) : Empty;

    /// <summary>YAML 構造に対応する DTO（snake_case）。</summary>
    public sealed class Dto
    {
        public string? AquestalkDevKey { get; set; }     // aquestalk_dev_key
        public string? AquestalkUsrKey { get; set; }     // aquestalk_usr_key
        public string? Aqkanji2koeDevKey { get; set; }   // aqkanji2koe_dev_key
    }
}
