using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class ProgramThemesConfigTests
{
    // --- program.yaml (v1, 明示 segments 列) ---

    [Fact]
    public void Program_FromYaml_LoadsSegmentsInOrder()
    {
        const string yaml =
            "program:\n" +
            "  title: \"テスト番組\"\n" +
            "  anchor_dj_id: zundamon\n" +
            "  segments:\n" +
            "    - type: opening\n      critical: true\n" +
            "    - type: talk\n      corner_id: free_talk\n" +
            "    - type: news\n" +
            "    - type: ending\n";

        var f = ProgramConfig.FromYaml(yaml);

        Assert.Equal("テスト番組", f.Title);
        Assert.Equal("zundamon", f.AnchorDjId);
        Assert.Equal(4, f.Segments.Count);
        Assert.Equal(new ProgramSegment(SegmentKind.Opening, null, true), f.Segments[0]);
        Assert.Equal(new ProgramSegment(SegmentKind.Talk, "free_talk", false), f.Segments[1]);
        Assert.Equal(new ProgramSegment(SegmentKind.News, null, false), f.Segments[2]);
        Assert.Equal(new ProgramSegment(SegmentKind.Ending, null, false), f.Segments[3]);
    }

    [Fact]
    public void Program_TitleDefaults_WhenOmitted()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  segments:\n    - type: opening\n";

        var f = ProgramConfig.FromYaml(yaml);

        Assert.Equal("ケイラボAIラジオ", f.Title);
        Assert.False(f.Segments[0].Critical); // critical 省略は false
    }

    [Fact]
    public void Program_MissingAnchorDj_ThrowsMissingField()
    {
        const string yaml = "program:\n  segments:\n    - type: opening\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_EmptySegments_ThrowsMissingField()
        => Assert.Throws<ConfigException>(
            () => ProgramConfig.FromYaml("program:\n  anchor_dj_id: z\n  segments: []\n"));

    [Fact]
    public void Program_TalkMissingCornerId_ThrowsMissingField()
    {
        const string yaml = "program:\n  anchor_dj_id: z\n  segments:\n    - type: talk\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_UnknownSegmentType_ThrowsMissingField()
    {
        const string yaml = "program:\n  anchor_dj_id: z\n  segments:\n    - type: bogus\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    // --- themes.yaml ---

    [Fact]
    public void Themes_FromYaml_LoadsThemes_NormalizesUri_AndApplies()
    {
        const string yaml =
            "opening:\n" +
            "  track_uri: \"https://open.spotify.com/track/ABC?si=x\"\n" +
            "  tagline: \"OPタグ\"\n  announcement: \"OP原稿\"\n" +
            "  intro_seconds: 3\n  volume: 90\n  ducked_volume: 20\n  outro_seconds: 8\n" +
            "news:\n  track_uri: \"spotify:track:NEWS\"\n  tagline: \"ニュースです\"\n" +
            "ending:\n  track_uri: \"DEF\"\n  announcement: \"ED原稿\"\n";

        var t = ThemesConfig.FromYaml(yaml);

        // OP: 共有 URL → spotify:track: に正規化。tagline / announcement / staging 反映。
        Assert.Equal("spotify:track:ABC", t.Opening.TrackUri);
        Assert.Equal("OPタグ", t.Opening.Tagline);
        Assert.Equal("OP原稿", t.OpeningAnnouncement);
        Assert.Equal(3, t.Opening.IntroSeconds);
        Assert.Equal(90, t.Opening.Volume);
        Assert.Equal(20, t.Opening.DuckedVolume);
        Assert.Equal(8, t.Opening.OutroSeconds);

        // news: staging のみ。省略値は既定（intro 5 / volume 100 / ducked 35 / outro 10）。
        Assert.Equal("spotify:track:NEWS", t.News.TrackUri);
        Assert.Equal("ニュースです", t.News.Tagline);
        Assert.Equal(5, t.News.IntroSeconds);
        Assert.Equal(100, t.News.Volume);
        Assert.Equal(35, t.News.DuckedVolume);
        Assert.Equal(10, t.News.OutroSeconds);

        // ED: tagline なし（いきなり BGM）。裸 ID も正規化。
        Assert.Null(t.Ending.Tagline);
        Assert.Equal("spotify:track:DEF", t.Ending.TrackUri);
        Assert.Equal("ED原稿", t.EndingAnnouncement);
    }

    [Fact]
    public void Themes_MissingTrackUri_ThrowsMissingField()
    {
        const string yaml =
            "opening:\n  tagline: \"x\"\n" +
            "news:\n  track_uri: \"spotify:track:N\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_MissingSection_ThrowsMissingField()
    {
        // news セクション欠落 → fail-fast。
        const string yaml =
            "opening:\n  track_uri: \"spotify:track:O\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n";

        Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
    }
}
