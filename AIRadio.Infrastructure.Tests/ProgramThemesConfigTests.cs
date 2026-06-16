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
    public void Program_ParsesTwoConsecutiveTalks_PreservesOrder()
    {
        // W12: OP → song → talk(free_talk) → talk(letter) → news → ED。連続 2 talk が別 corner_id を順序保持で読まれる。
        const string yaml =
            "program:\n  title: \"テスト番組\"\n  anchor_dj_id: zundamon\n  segments:\n" +
            "    - type: opening\n      critical: true\n" +
            "    - type: talk\n      corner_id: free_talk\n" +
            "    - type: talk\n      corner_id: letter\n" +
            "    - type: news\n" +
            "    - type: ending\n";

        var f = ProgramConfig.FromYaml(yaml);

        Assert.Equal(new ProgramSegment(SegmentKind.Talk, "free_talk", false), f.Segments[1]);
        Assert.Equal(new ProgramSegment(SegmentKind.Talk, "letter", false), f.Segments[2]);
        Assert.Equal(SegmentKind.News, f.Segments[3].Kind);
        Assert.Equal(SegmentKind.Ending, f.Segments[4].Kind);
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

    [Fact]
    public void Program_ParsesSongSegment_NormalizesUri()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  segments:\n" +
            "    - type: song\n      song_prompt_hint: \"幕開け\"\n" +
            "      fallback_track_uri: \"https://open.spotify.com/track/ABC?si=x\"\n" +
            "      volume: 90\n      play_seconds: 30\n";

        var f = ProgramConfig.FromYaml(yaml);

        var song = f.Segments[0];
        Assert.Equal(SegmentKind.Song, song.Kind);
        Assert.NotNull(song.Song);
        Assert.Equal("spotify:track:ABC", song.Song!.FallbackTrackUri); // 共有 URL → 正規化
        Assert.Equal("幕開け", song.Song.PromptHint);
        Assert.Equal(90, song.Song.Volume);
        Assert.Equal(30, song.Song.PlaySeconds);
    }

    [Fact]
    public void Program_SongMissingFallbackUri_ThrowsMissingField()
    {
        const string yaml = "program:\n  anchor_dj_id: z\n  segments:\n    - type: song\n";

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

    // --- themes.yaml greetings（W8） ---

    private const string MinimalThemes =
        "opening:\n  track_uri: \"spotify:track:O\"\n  announcement: \"OP\"\n" +
        "news:\n  track_uri: \"spotify:track:N\"\n" +
        "ending:\n  track_uri: \"spotify:track:E\"\n  announcement: \"ED\"\n";

    [Fact]
    public void Themes_LoadsGreetings_WhenPresent()
    {
        const string yaml = MinimalThemes +
            "greetings:\n  morning: \"おはよ\"\n  afternoon: \"ちは\"\n  evening: \"ばんは\"\n";

        var t = ThemesConfig.FromYaml(yaml);

        Assert.Equal("おはよ", t.Greetings.Morning);
        Assert.Equal("ちは", t.Greetings.Afternoon);
        Assert.Equal("ばんは", t.Greetings.Evening);
    }

    [Fact]
    public void Themes_GreetingsDefault_WhenBlockOmitted()
    {
        // greetings ブロックごと欠落 → 既定の挨拶（fail-tolerant、空挨拶を読まない）。
        var t = ThemesConfig.FromYaml(MinimalThemes);

        Assert.Equal("おはようございます", t.Greetings.Morning);
        Assert.Equal("こんにちは", t.Greetings.Afternoon);
        Assert.Equal("こんばんは", t.Greetings.Evening);
    }

    [Fact]
    public void Themes_GreetingsPartial_FillsMissingWithDefault()
    {
        // afternoon のみ指定 → 残り 2 つ（キー欠落）は既定を維持。
        const string yaml = MinimalThemes + "greetings:\n  afternoon: \"やあ\"\n";

        var t = ThemesConfig.FromYaml(yaml);

        Assert.Equal("おはようございます", t.Greetings.Morning); // 欠落 → 既定
        Assert.Equal("やあ", t.Greetings.Afternoon);            // 上書き
        Assert.Equal("こんばんは", t.Greetings.Evening);        // 欠落 → 既定
    }

    [Fact]
    public void Themes_GreetingsEmptyString_KeptAsIs_OmittedKeyDefaults()
    {
        // Mac 一致: 明示的な空文字はそのまま採用（既定に倒さない）。倒すのはキー欠落（null）のみ。
        const string yaml = MinimalThemes + "greetings:\n  morning: \"\"\n  afternoon: \"やあ\"\n"; // evening はキー欠落

        var t = ThemesConfig.FromYaml(yaml);

        Assert.Equal("", t.Greetings.Morning);            // 明示的空文字はそのまま（Mac 一致）
        Assert.Equal("やあ", t.Greetings.Afternoon);       // 上書き
        Assert.Equal("こんばんは", t.Greetings.Evening);   // 欠落（null）→ 既定
    }
}
