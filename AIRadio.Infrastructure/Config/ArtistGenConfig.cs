using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// アーティスト特集の生成設定（<c>config/artist-gen.yaml</c>。W15 §9）。メニュー「アーティスト一覧を生成」が読む入力。
/// ジャンル・件数をハードコードせずここで変える（これは入力。生成結果は <c>config/artists.yaml</c> に上書きされる）。
/// 欠落フィールドは既定値、ファイル無し・空は全既定（Mac <c>ArtistGenConfigLoader</c> 一致）。
/// </summary>
/// <param name="GenrePrompt">LLM に渡すジャンル/スコープの自由記述（邦楽 / 洋楽 / クラシック等）。</param>
/// <param name="TargetCount">プールに保存したい組数（目標。20 でも 100 でも可）。</param>
public sealed record ArtistGenConfig(
    string GenrePrompt = "日本の音楽アーティスト（邦楽）。バンド・ソロ・グループを問わず、誰もが知る有名どころ中心。",
    int TargetCount = 100)
{
    public static ArtistGenConfig FromYaml(string yaml)
    {
        var defaults = new ArtistGenConfig();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return defaults;
        }
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var gen = dto?.Generation;
        var prompt = string.IsNullOrEmpty(gen?.GenrePrompt) ? defaults.GenrePrompt : gen!.GenrePrompt!;
        var count = gen?.TargetCount ?? defaults.TargetCount;
        // target_count < 1 は既定（100）にクランプ（Mac 一致。1 でも throw でもない）。
        return new ArtistGenConfig(prompt, count >= 1 ? count : defaults.TargetCount);
    }

    /// <summary>ファイル無しは全既定（Mac の <c>try?</c> 相当）。</summary>
    public static ArtistGenConfig LoadFile(string path) =>
        File.Exists(path) ? FromYaml(File.ReadAllText(path)) : new ArtistGenConfig();

    public sealed class Dto
    {
        public GenerationDto? Generation { get; set; }
    }

    public sealed class GenerationDto
    {
        public string? GenrePrompt { get; set; }
        public int? TargetCount { get; set; }
    }
}
