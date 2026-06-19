using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/banter.yaml</c> のローダ（掛け合い指示 → <see cref="BanterDirectives"/>。W-DLG）。
/// 任意機能ゆえ <b>ファイル不在 → <see cref="BanterDirectives.Empty"/></b>（tolerant・防御的ロード）。
/// 各エントリは <c>main</c> / <c>directive</c> 非空が必須（欠落は <c>E-CFG-MISSING-FIELD-001</c> fail-fast）。<c>requires</c> は任意（既定 空）。
/// </summary>
public static class BanterConfig
{
    public static BanterDirectives FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var entries = dto?.Banter;
        if (entries is null || entries.Count == 0)
        {
            return BanterDirectives.Empty; // banter: 欠落 / 空 → 注入なし（tolerant）。
        }
        return new BanterDirectives(entries.Select(Map).ToList());
    }

    public static BanterDirectives LoadFile(string path) =>
        File.Exists(path) ? FromYaml(File.ReadAllText(path)) : BanterDirectives.Empty;

    private static BanterRule Map(BanterEntryDto e)
    {
        if (string.IsNullOrEmpty(e.Main))
        {
            throw ConfigException.MissingField("banter[].main");
        }
        if (string.IsNullOrEmpty(e.Directive))
        {
            throw ConfigException.MissingField($"banter エントリ（main={e.Main}）の directive");
        }
        return new BanterRule(e.Main, e.Requires ?? new List<string>(), e.Directive);
    }

    public sealed class Dto
    {
        public List<BanterEntryDto>? Banter { get; set; }
    }

    public sealed class BanterEntryDto
    {
        public string? Main { get; set; }
        public List<string>? Requires { get; set; }
        public string? Directive { get; set; }
    }
}
