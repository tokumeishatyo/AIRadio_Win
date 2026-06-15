using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// ニュース・天気のリサーチ設定（`config/research.yaml`）。LLM 原稿設定（`llm_script`）は W11 用のため本スライスでは読み飛ばす。
/// </summary>
public sealed record ResearchConfig(
    string NewsRssUrl,
    int NewsMaxItems,
    string WeatherAreaCode,
    string WeatherAreaName,
    string AnnouncementTemplate)
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

        return new ResearchConfig(
            NewsRssUrl: dto!.News?.RssUrl ?? DefaultRssUrl,
            NewsMaxItems: dto.News?.MaxItems ?? 5,
            WeatherAreaCode: areaCode,
            WeatherAreaName: dto.Weather?.AreaName ?? "対象地域",
            AnnouncementTemplate: dto.AnnouncementTemplate ?? DefaultTemplate);
    }

    public static ResearchConfig LoadFile(string path) => FromYaml(File.ReadAllText(path));

    public sealed class Dto
    {
        public NewsDto? News { get; set; }
        public WeatherDto? Weather { get; set; }
        public string? AnnouncementTemplate { get; set; }

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
    }
}
