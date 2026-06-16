using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// ニュース・天気のリサーチ設定（`config/research.yaml`）。LLM 原稿設定（`llm_script`）は <see cref="LlmScript"/> に読み込む（W11）。
/// </summary>
public sealed record ResearchConfig(
    string NewsRssUrl,
    int NewsMaxItems,
    string WeatherAreaCode,
    string WeatherAreaName,
    string AnnouncementTemplate,
    NewsScriptStyle LlmScript)
{
    private const string DefaultRssUrl = "https://news.google.com/rss?hl=ja&gl=JP&ceid=JP:ja";
    private const string DefaultTemplate =
        "本日のニュースをお伝えします。{news} 続いて天気予報です。{weather} 以上、ニュースと天気でした。";

    public static ResearchConfig FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var areaCode = dto?.Weather?.AreaCode;
        if (string.IsNullOrEmpty(areaCode))
        {
            throw ConfigException.MissingField("weather.area_code");
        }

        var styleDefaults = new NewsScriptStyle();
        var llm = dto!.LlmScript;
        return new ResearchConfig(
            NewsRssUrl: dto.News?.RssUrl ?? DefaultRssUrl,
            NewsMaxItems: dto.News?.MaxItems ?? 5,
            WeatherAreaCode: areaCode,
            WeatherAreaName: dto.Weather?.AreaName ?? "対象地域",
            AnnouncementTemplate: dto.AnnouncementTemplate ?? DefaultTemplate,
            LlmScript: new NewsScriptStyle(
                StyleHint: llm?.StyleHint ?? styleDefaults.StyleHint,
                TargetMinutes: llm?.TargetMinutes ?? styleDefaults.TargetMinutes,
                CharsPerMinute: llm?.CharsPerMinute ?? styleDefaults.CharsPerMinute,
                Intro: llm?.Intro ?? styleDefaults.Intro,
                Outro: llm?.Outro ?? styleDefaults.Outro));
    }

    public static ResearchConfig LoadFile(string path) => FromYaml(File.ReadAllText(path));

    public sealed class Dto
    {
        public NewsDto? News { get; set; }
        public WeatherDto? Weather { get; set; }
        public string? AnnouncementTemplate { get; set; }
        public LlmScriptDto? LlmScript { get; set; }

        public sealed class NewsDto
        {
            public string? RssUrl { get; set; }
            public int? MaxItems { get; set; }
        }

        public sealed class WeatherDto
        {
            public string? AreaCode { get; set; }
            public string? AreaName { get; set; }
        }

        public sealed class LlmScriptDto
        {
            public string? StyleHint { get; set; }
            public int? TargetMinutes { get; set; }
            public int? CharsPerMinute { get; set; }
            public string? Intro { get; set; }
            public string? Outro { get; set; }
        }
    }
}

/// <summary>
/// ニュース LLM 原稿のスタイル設定（W11。<c>config/research.yaml</c> の <c>llm_script:</c>）。
/// 時報イントロ・締めのアウトロは固定文（正確性が必要なため LLM に渡さず、発話直前の時刻展開だけ受ける）。
/// Mac 版 <c>NewsScriptStyle</c> の移植。
/// </summary>
public sealed record NewsScriptStyle(
    string StyleHint = "",
    int TargetMinutes = 2,
    int CharsPerMinute = 320,
    string Intro = "時刻は{hour12}時{minute}分になりました。ニュースの時間です。",
    string Outro = "以上、ニュースと天気予報でした。")
{
    /// <summary>本文の目安文字数（分 × 1 分あたり文字数）。</summary>
    public int TargetCharacters => TargetMinutes * CharsPerMinute;
}
