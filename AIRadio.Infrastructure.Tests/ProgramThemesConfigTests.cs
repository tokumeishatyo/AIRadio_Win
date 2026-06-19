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
    public void Program_ReadsArtistFeatureCornerId()
    {
        // artist_feature（W15）の corner_id を読む（W14 までの「無視」を反転）。
        const string yaml = MinimalProgram + "  artist_feature:\n    corner_id: artist_feature\n";

        var b = ProgramConfig.FromYaml(yaml);
        Assert.Equal("artist_feature", b.ArtistFeatureCornerId);
    }

    [Fact]
    public void Program_ArtistFeatureOmitted_Null()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram);
        Assert.Null(b.ArtistFeatureCornerId);
    }

    [Fact]
    public void Program_ReadsGuestCornerId()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram + "  guest:\n    corner_id: guest\n");
        Assert.Equal("guest", b.GuestCornerId);
    }

    [Fact]
    public void Program_GuestOmitted_GuestCornerIdNull()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram);
        Assert.Null(b.GuestCornerId);
    }

    // --- program.yaml weekly_cast（W13.5） ---

    [Fact]
    public void Program_ParsesWeeklyCast()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram +
            "  weekly_cast:\n    monday: [zundamon, metan]\n    sunday: [zundamon, metan, tsumugi]\n");

        Assert.Equal(new[] { "zundamon", "metan" }, b.WeeklyCast.Casts[DayOfWeek.Monday]);
        Assert.Equal(new[] { "zundamon", "metan", "tsumugi" }, b.WeeklyCast.Casts[DayOfWeek.Sunday]);
    }

    [Fact]
    public void Program_WeeklyCastOmitted_UsesStandard()
    {
        var b = ProgramConfig.FromYaml(MinimalProgram);   // weekly_cast 省略

        Assert.Same(WeeklyCast.Standard, b.WeeklyCast);
        Assert.Equal(3, b.WeeklyCast.Casts[DayOfWeek.Sunday].Count); // 日曜 3 人
    }

    [Fact]
    public void Program_WeeklyCastInvalidDay_ThrowsMissingField()
    {
        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(
            MinimalProgram + "  weekly_cast:\n    funday: [zundamon]\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Program_WeeklyCastEmptyCast_ThrowsMissingField()
    {
        var ex = Assert.Throws<ConfigException>(() => ProgramConfig.FromYaml(
            MinimalProgram + "  weekly_cast:\n    monday: []\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    // --- themes.yaml（W13.5: OP/ED は by_dj、news は単一） ---

    [Fact]
    public void Themes_FromYaml_LoadsByDjThemes_NormalizesUri_AndApplies()
    {
        const string yaml =
            "opening:\n" +
            "  track_uri: \"https://open.spotify.com/track/ABC?si=x\"\n" +
            "  intro_seconds: 3\n  volume: 90\n  ducked_volume: 20\n  outro_seconds: 8\n" +
            "  by_dj:\n" +
            "    zundamon:\n      tagline: \"OPタグず\"\n      announcement: \"OP原稿ず\"\n" +
            "    metan:\n      tagline: \"OPタグめ\"\n      announcement: \"OP原稿め\"\n" +
            "news:\n  track_uri: \"spotify:track:NEWS\"\n  tagline: \"ニュースです\"\n" +
            "ending:\n  track_uri: \"DEF\"\n" +
            "  by_dj:\n    zundamon:\n      announcement: \"ED原稿ず\"\n";

        var t = ThemesConfig.FromYaml(yaml);

        // OP staging: 共有 URL → 正規化。tagline は staging では持たない（per-DJ のため null）。
        Assert.Equal("spotify:track:ABC", t.Opening.Staging.TrackUri);
        Assert.Null(t.Opening.Staging.Tagline);
        Assert.Equal(3, t.Opening.Staging.IntroSeconds);
        Assert.Equal(90, t.Opening.Staging.Volume);
        Assert.Equal(20, t.Opening.Staging.DuckedVolume);
        Assert.Equal(8, t.Opening.Staging.OutroSeconds);
        // OP by_dj: DJ 別 tagline / announcement。
        Assert.Equal("OP原稿ず", t.Opening.ByDj["zundamon"].Announcement);
        Assert.Equal("OPタグず", t.Opening.ByDj["zundamon"].Tagline);
        Assert.Equal("OP原稿め", t.Opening.ByDj["metan"].Announcement);

        // news: 単一（tagline 保持）。省略値は既定。
        Assert.Equal("spotify:track:NEWS", t.News.TrackUri);
        Assert.Equal("ニュースです", t.News.Tagline);
        Assert.Equal(5, t.News.IntroSeconds);
        Assert.Equal(100, t.News.Volume);

        // ED: by_dj（tagline なし＝null）。裸 ID も正規化。
        Assert.Equal("spotify:track:DEF", t.Ending.Staging.TrackUri);
        Assert.Equal("ED原稿ず", t.Ending.ByDj["zundamon"].Announcement);
        Assert.Null(t.Ending.ByDj["zundamon"].Tagline);
    }

    [Fact]
    public void Themes_MissingTrackUri_ThrowsMissingField()
    {
        const string yaml =
            "opening:\n  by_dj:\n    zundamon:\n      announcement: \"x\"\n" +  // track_uri 欠落
            "news:\n  track_uri: \"spotify:track:N\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n  by_dj:\n    zundamon:\n      announcement: \"y\"\n";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_MissingByDj_ThrowsMissingField()
    {
        // OP に by_dj が無い → fail-fast（W13.5）。
        const string yaml =
            "opening:\n  track_uri: \"spotify:track:O\"\n" +
            "news:\n  track_uri: \"spotify:track:N\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n  by_dj:\n    zundamon:\n      announcement: \"y\"\n";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_ByDjMissingAnnouncement_ThrowsMissingField()
    {
        // by_dj の spiel に announcement が無い → fail-fast（W13.5）。
        const string yaml =
            "opening:\n  track_uri: \"spotify:track:O\"\n  by_dj:\n    zundamon:\n      tagline: \"t\"\n" +
            "news:\n  track_uri: \"spotify:track:N\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n  by_dj:\n    zundamon:\n      announcement: \"y\"\n";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_MissingSection_ThrowsMissingField()
    {
        // news セクション欠落 → fail-fast。
        const string yaml =
            "opening:\n  track_uri: \"spotify:track:O\"\n  by_dj:\n    zundamon:\n      announcement: \"a\"\n" +
            "ending:\n  track_uri: \"spotify:track:E\"\n  by_dj:\n    zundamon:\n      announcement: \"b\"\n";

        Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
    }

    // --- themes.yaml greetings（W8） ---

    private const string MinimalThemes =
        "opening:\n  track_uri: \"spotify:track:O\"\n  by_dj:\n    zundamon:\n      announcement: \"OP\"\n" +
        "news:\n  track_uri: \"spotify:track:N\"\n" +
        "ending:\n  track_uri: \"spotify:track:E\"\n  by_dj:\n    zundamon:\n      announcement: \"ED\"\n";

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

    // --- themes.yaml 多声 OP 台本（W-OP: script / simultaneous） ---

    [Fact]
    public void Themes_Script_ParsesSingleAndSimultaneous()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script:
        - speaker: reimu
          line: "L1"
        - simultaneous:
            - speaker: reimu
              line: "S1"
            - speaker: marisa
              line: "S2"
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var t = ThemesConfig.FromYaml(yaml);
        var reimu = t.Opening.ByDj["reimu"];

        Assert.True(reimu.HasScript);
        Assert.Equal("", reimu.Announcement); // script 時は空
        Assert.Equal(2, reimu.Script!.Count);
        // step 0: 単独行。
        Assert.Single(reimu.Script[0].Voices);
        Assert.Equal(new SpielLine("reimu", "L1"), reimu.Script[0].Voices[0]);
        // step 1: 同時発話（2 声）。
        Assert.Equal(2, reimu.Script[1].Voices.Count);
        Assert.Equal(new SpielLine("reimu", "S1"), reimu.Script[1].Voices[0]);
        Assert.Equal(new SpielLine("marisa", "S2"), reimu.Script[1].Voices[1]);
    }

    [Fact]
    public void Themes_AnnouncementAndScriptBoth_ThrowsMissingField()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      announcement: "A"
      script:
        - speaker: reimu
          line: "L"
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_EmptySimultaneousGroup_ThrowsMissingField()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script:
        - simultaneous: []
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_ScriptLineMissingSpeaker_ThrowsMissingField()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script:
        - line: "L (speaker 無し)"
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_ScriptStepMissingLine_ThrowsMissingField()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script:
        - speaker: reimu
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_SimultaneousEntryMissingSpeaker_ThrowsMissingField()
    {
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script:
        - simultaneous:
            - speaker: reimu
              line: "S1"
            - line: "S2 (speaker 無し)"
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_ScriptEmptyList_TreatedAsAbsent_ThrowsMissingField()
    {
        // script: [] は script 不在扱い → announcement も無いので XOR 不成立で fail-fast。
        const string yaml =
"""
opening:
  track_uri: "spotify:track:O"
  by_dj:
    reimu:
      script: []
news:
  track_uri: "spotify:track:N"
ending:
  track_uri: "spotify:track:E"
  by_dj:
    zundamon:
      announcement: "ED"
""";

        var ex = Assert.Throws<ConfigException>(() => ThemesConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Themes_AnnouncementOnly_HasNoScript_BackwardCompatible()
    {
        // 既存の announcement のみのエントリは Script=null（後方互換）。
        var t = ThemesConfig.FromYaml(MinimalThemes);
        Assert.False(t.Opening.ByDj["zundamon"].HasScript);
        Assert.Null(t.Opening.ByDj["zundamon"].Script);
        Assert.Equal("OP", t.Opening.ByDj["zundamon"].Announcement);
    }

    [Fact]
    public void Themes_ProductionThemesYaml_LoadsWithReimuScript()
    {
        // 実 config/themes.yaml をロードし、reimu が多声 OP（script）・他 DJ は announcement（Script=null）であることを検証。
        var t = ThemesConfig.LoadFile(RepoConfig("themes.yaml"));

        Assert.True(t.Opening.ByDj["reimu"].HasScript);
        Assert.False(t.Opening.ByDj["zundamon"].HasScript);
        Assert.False(t.Opening.ByDj["metan"].HasScript);
        Assert.False(t.Opening.ByDj["tsumugi"].HasScript);

        var script = t.Opening.ByDj["reimu"].Script!;
        Assert.Equal(5, script.Count);
        Assert.Equal("reimu", script[0].Voices[0].Speaker);   // ① 霊夢 名乗り
        Assert.Equal("marisa", script[1].Voices[0].Speaker);  // ② 魔理沙 名乗り
        Assert.Single(script[2].Voices);                      // ③ 本編（霊夢単独・クレジット）
        Assert.Equal(2, script[3].Voices.Count);              // ④ 二人同時の締め
        Assert.Single(script[4].Voices);                      // ⑤ 曲振り（霊夢単独・{first_song} で終止）
        Assert.Equal("reimu", script[4].Voices[0].Speaker);
    }

    /// <summary>テスト実行ディレクトリから親を辿って実 config/&lt;name&gt; を見つける（本番設定の実ロード検証用）。</summary>
    private static string RepoConfig(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"config/{name} が見つかりません（リポジトリ構成を確認）。");
    }
}
