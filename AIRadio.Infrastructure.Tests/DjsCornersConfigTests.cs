using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class DjsCornersConfigTests
{
    [Fact]
    public void Djs_FromYaml_LoadsProfiles()
    {
        const string yaml =
            "djs:\n" +
            "  - id: zundamon\n    name: \"ずんだもん\"\n    speaker_id: 3\n    persona: \"〜なのだ\"\n" +
            "  - id: metan\n    name: \"四国めたん\"\n    speaker_id: 2\n    persona: \"上品\"\n";

        var djs = DjsConfig.FromYaml(yaml);

        Assert.Equal(2, djs.Count);
        Assert.Equal(new DjProfile("zundamon", "ずんだもん", 3, "〜なのだ"), djs[0]);
        Assert.Equal(2, djs[1].SpeakerId);
    }

    [Fact]
    public void Djs_MissingSpeakerId_ThrowsMissingField()
    {
        const string yaml = "djs:\n  - id: x\n    name: \"X\"\n    persona: \"p\"\n";

        var ex = Assert.Throws<ConfigException>(() => DjsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Djs_Empty_ThrowsMissingField()
        => Assert.Throws<ConfigException>(() => DjsConfig.FromYaml("djs: []\n"));

    [Fact]
    public void Corners_FromYaml_LoadsTemplates_AndMapsFormat()
    {
        const string yaml =
            "corners:\n" +
            "  - id: free_talk\n    title: \"フリートーク\"\n    theme: \"音楽\"\n    format: free_talk\n" +
            "    dj_ids: [zundamon, metan]\n    target_minutes: 5\n    chars_per_minute: 320\n" +
            "    song_prompt_hint: \"邦楽\"\n    fallback_track_uri: \"spotify:track:fb\"\n" +
            "    volume: 100\n    play_seconds: 0\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.Single(corners);
        var c = corners[0];
        Assert.Equal("free_talk", c.Id);
        Assert.Equal(CornerFormat.FreeTalk, c.Format);
        Assert.Equal(new[] { "zundamon", "metan" }, c.DjIds);
        Assert.Equal("spotify:track:fb", c.FallbackTrackUri);
        Assert.Equal(100, c.Volume);
        Assert.Equal(1600, c.TargetCharacters); // 5 × 320
    }

    [Fact]
    public void Corners_IgnoresUnknownFields_AndMapsLetterFormat()
    {
        // themes / lead_in は W12/W13.5 用。読み飛ばしてロードできる。format=letter は enum へマップ。
        const string yaml =
            "corners:\n" +
            "  - id: letter\n    title: \"お便り\"\n    format: letter\n    theme: \"x\"\n" +
            "    lead_in: \"{hour}時です\"\n    themes: [\"a\", \"b\"]\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.Equal(CornerFormat.Letter, corners[0].Format);
    }

    [Fact]
    public void Corners_MissingFallbackUri_ThrowsMissingField()
    {
        const string yaml = "corners:\n  - id: x\n    title: \"X\"\n    dj_ids: [a]\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Corners_UnknownFormat_ThrowsMissingField()
    {
        // 不正な format 値は fail-fast で拒否（§4-3 起動時設定不正）。
        const string yaml =
            "corners:\n  - id: x\n    title: \"X\"\n    format: bogus\n" +
            "    dj_ids: [a]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
