using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class ProgramThemesConfigTests
{
    // --- program.yaml (v2 = 部品宣言) ---

    private const string MinimalProgram =
        "program:\n" +
        "  anchor_dj_id: zundamon\n" +
        "  song:\n    fallback_track_uri: \"spotify:track:X\"\n" +
        "  talk:\n    corner_id: free_talk\n" +
        "  letter:\n    corner_id: letter\n";

    [Fact]
    public void Program_FromYaml_LoadsBlueprint()
    {
        const string yaml =
            "program:\n" +
            "  title: \"テスト番組\"\n" +
            "  anchor_dj_id: zundamon\n" +
            "  default_length: 20\n" +
            "  opening:\n    critical: true\n" +
            "  song:\n" +
            "    song_prompt_hint: \"幕開け\"\n" +
            "    fallback_track_uri: \"https://open.spotify.com/track/ABC?si=x\"\n" +
            "    volume: 90\n    play_seconds: 30\n" +
            "  talk:\n    corner_id: free_talk\n" +
            "  letter:\n    corner_id: letter\n" +
            "  news:\n    dj_id: ryusei\n";

        var b = ProgramConfig.FromYaml(yaml);

        Assert.Equal("テスト番組", b.Title);
        Assert.Equal("zundamon", b.AnchorDjId);
        Assert.Equal(ProgramLength.FromCorners(20), b.DefaultLength);
        Assert.True(b.OpeningCritical);
        Assert.Equal("spotify:track:ABC", b.Song.FallbackTrackUri);   // 共有 URL → 正規化
        Assert.Equal("幕開け", b.Song.PromptHint);
        Assert.Equal(90, b.Song.Volume);
        Assert.Equal(30, b.Song.PlaySeconds);
        Assert.Equal("free_talk", b.TalkCornerId);
        Assert.Equal("letter", b.LetterCornerId);
        Assert.Equal("ryusei", b.NewsDjId);
    }

    [Theory]
    [InlineData("  default_length: 10\n", 10)]
    [InlineData("  default_length: \"30\"\n", 30)]   // 文字列スカラも可
    public void Program_DefaultLength_IntOrStringCorners(string line, int corners)
    {
        var b = ProgramConfig.FromYaml(MinimalProgram + line);
        Assert.Equal(ProgramLength.FromCorners(corners), b.DefaultLength);
    }

    [Fact]
    public void Program_DefaultLength_Endless()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram + "  default_length: endless\n");
        Assert.True(b.DefaultLength.IsEndless);
    }

    [Fact]
    public void Program_DefaultLength_Missing_DefaultsTo10()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram);   // default_length なし
        Assert.Equal(ProgramLength.FromCorners(10), b.DefaultLength);
    }

    [Theory]
    [InlineData("  default_length: 0\n")]        // 0 は不正（1 以上）
    [InlineData("  default_length: -1\n")]       // 負数
    [InlineData("  default_length: abc\n")]      // 非数値
    [InlineData("  default_length: 10.5\n")]     // 小数
    [InlineData("  default_length: yes\n")]      // YAML 1.1 特殊スカラ（string 束縛 → TryParse 拒否）
    [InlineData("  default_length: \"\"\n")]     // 明示的空文字（欠落と区別して fail-fast）
    public void Program_DefaultLength_Invalid_FailsFast(string line)
    {
        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(MinimalProgram + line));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_TitleDefaults_OpeningCriticalTrue_OptionalsDefault()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram);   // title / opening / news / volume 省略

        Assert.Equal("ケイラボAIラジオ", b.Title);
        Assert.True(b.OpeningCritical);   // 既定 true（Windows 踏襲）
        Assert.Equal(100, b.Song.Volume); // 既定 100
        Assert.Equal(0, b.Song.PlaySeconds);
        Assert.Null(b.NewsDjId);          // 任意
    }

    [Fact]
    public void Program_MissingAnchorDj_ThrowsMissingField()
    {
        const string yaml =
            "program:\n  song:\n    fallback_track_uri: \"spotify:track:X\"\n" +
            "  talk:\n    corner_id: free_talk\n  letter:\n    corner_id: letter\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_MissingSongFallbackUri_ThrowsMissingField()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  talk:\n    corner_id: free_talk\n  letter:\n    corner_id: letter\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_MissingTalkCorner_ThrowsMissingField()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  song:\n    fallback_track_uri: \"spotify:track:X\"\n" +
            "  letter:\n    corner_id: letter\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_MissingLetterCorner_ThrowsMissingField()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  song:\n    fallback_track_uri: \"spotify:track:X\"\n" +
            "  talk:\n    corner_id: free_talk\n";

        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_NormalizesBareTrackId()
    {
        const string yaml =
            "program:\n  anchor_dj_id: zundamon\n  song:\n    fallback_track_uri: \"ABC\"\n" +
            "  talk:\n    corner_id: free_talk\n  letter:\n    corner_id: letter\n";

        var b = ProgramConfig.FromYaml(yaml);
        Assert.Equal("spotify:track:ABC", b.Song.FallbackTrackUri);
    }

    [Fact]
    public void Program_IgnoresFutureSliceKeys()
    {
        // weekly_cast / guest / artist_feature（W13.5/W14/W15）は v2 では無視され壊れない。
        const string yaml = MinimalProgram +
            "  weekly_cast:\n    monday: [zundamon, metan]\n" +
            "  guest:\n    corner_id: guest_corner\n" +
            "  artist_feature:\n    corner_id: feature\n";

        var b = ProgramConfig.FromYaml(yaml);
        Assert.Equal("free_talk", b.TalkCornerId);   // 正常ロード（未知キーは無視）
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
