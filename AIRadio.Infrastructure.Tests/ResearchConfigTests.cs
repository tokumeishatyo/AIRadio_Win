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
    public void FromYaml_LoadsLlmScript_WhenPresent()
    {
        // llm_script（W11）を NewsScriptStyle に読み込む。intro/outro 省略は既定（固定文）。
        const string yaml =
            "weather:\n" +
            "  area_code: \"130000\"\n" +
            "llm_script:\n" +
            "  style_hint: \"落ち着いたトーン\"\n" +
            "  target_minutes: 3\n" +
            "  chars_per_minute: 300\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal("落ち着いたトーン", config.LlmScript.StyleHint);
        Assert.Equal(3, config.LlmScript.TargetMinutes);
        Assert.Equal(300, config.LlmScript.CharsPerMinute);
        Assert.Equal(900, config.LlmScript.TargetCharacters);                 // 3 * 300
        Assert.Contains("{hour12}", config.LlmScript.Intro);                  // 省略 → 既定の時報イントロ
        Assert.Equal("以上、ニュースと天気予報でした。", config.LlmScript.Outro); // 省略 → 既定アウトロ
    }

    [Fact]
    public void FromYaml_LlmScriptDefaults_WhenOmitted()
    {
        const string yaml = "weather:\n  area_code: \"016000\"\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal("", config.LlmScript.StyleHint);
        Assert.Equal(2, config.LlmScript.TargetMinutes);
        Assert.Equal(320, config.LlmScript.CharsPerMinute);
        Assert.Equal(640, config.LlmScript.TargetCharacters);                 // 既定 2 * 320
    }

    [Fact]
    public void FromYaml_LlmScript_OverridesIntroAndOutro_AndMixesWithDefaults()
    {
        // intro/outro は YAML で上書き可（spec §4）。style_hint/target_minutes 省略は既定（混在）。
        const string yaml =
            "weather:\n  area_code: \"130000\"\n" +
            "llm_script:\n" +
            "  intro: \"カスタムイントロ。\"\n" +
            "  outro: \"カスタムアウトロ。\"\n";

        var config = ResearchConfig.FromYaml(yaml);

        Assert.Equal("カスタムイントロ。", config.LlmScript.Intro);     // 上書き
        Assert.Equal("カスタムアウトロ。", config.LlmScript.Outro);     // 上書き
        Assert.Equal("", config.LlmScript.StyleHint);                  // 省略 → 既定
        Assert.Equal(2, config.LlmScript.TargetMinutes);              // 省略 → 既定
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
