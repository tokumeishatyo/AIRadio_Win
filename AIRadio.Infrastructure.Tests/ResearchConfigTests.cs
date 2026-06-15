using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class ResearchConfigTests
{
    [Fact]
    public void FromYaml_LoadsAllFields()
    {
        const string yaml =
            "news:\n" +
            "  rss_url: \"https://example.com/rss\"\n" +
            "  max_items: 3\n" +
            "weather:\n" +
            "  area_code: \"270000\"\n" +
            "  area_name: \"大阪府\"\n" +
            "announcement_template: \"{news}/{weather}\"\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal("https://example.com/rss", config.NewsRssUrl);
        Assert.Equal(3, config.NewsMaxItems);
        Assert.Equal("270000", config.WeatherAreaCode);
        Assert.Equal("大阪府", config.WeatherAreaName);
        Assert.Equal("{news}/{weather}", config.AnnouncementTemplate);
    }

    [Fact]
    public void FromYaml_IgnoresUnknownLlmScriptSection()
    {
        // llm_script は W11 用。読み飛ばされてロードは成功する。
        const string yaml =
            "weather:\n" +
            "  area_code: \"130000\"\n" +
            "llm_script:\n" +
            "  style_hint: \"落ち着いたトーン\"\n" +
            "  target_minutes: 2\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal("130000", config.WeatherAreaCode);
    }

    [Fact]
    public void FromYaml_AppliesDefaults_WhenOptionalFieldsOmitted()
    {
        const string yaml = "weather:\n  area_code: \"016000\"\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal(5, config.NewsMaxItems);                         // 既定 5
        Assert.Contains("news.google.com", config.NewsRssUrl);        // 既定 RSS
        Assert.Equal("対象地域", config.WeatherAreaName);              // 既定エリア名
        Assert.Contains("{news}", config.AnnouncementTemplate);       // 既定テンプレ
    }

    [Fact]
    public void FromYaml_MissingAreaCode_ThrowsMissingField()
    {
        const string yaml = "news:\n  max_items: 5\n";

        var ex = Assert.Throws<ConfigException>(() => ResearchConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
