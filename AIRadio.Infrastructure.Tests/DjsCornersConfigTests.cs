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
    public void Corners_LoadsThemePool_LeadIn_AndMapsLetterFormat()
    {
        // themes（W12）を ThemePool に、lead_in（W13.5）を LeadIn に読み込む。format=letter は enum へマップ。
        const string yaml =
            "corners:\n" +
            "  - id: letter\n    title: \"お便り\"\n    format: letter\n    theme: \"x\"\n" +
            "    lead_in: \"{hour}時です\"\n    themes: [\"a\", \"b\", \"c\"]\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.Equal(CornerFormat.Letter, corners[0].Format);
        Assert.Equal(new[] { "a", "b", "c" }, corners[0].ThemePool); // themes → ThemePool
        Assert.Equal("{hour}時です", corners[0].LeadIn);              // lead_in → LeadIn（W13.5）
    }

    [Fact]
    public void Corners_NoLeadIn_LeadInNull()
    {
        // lead_in 未指定 → LeadIn は null（既定。Mac の "" とは異なる Win の意図的差異）。
        const string yaml =
            "corners:\n  - id: free_talk\n    title: \"フリートーク\"\n    theme: \"音楽\"\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.Null(corners[0].LeadIn);
    }

    [Fact]
    public void Corners_NoThemes_ThemePoolNull()
    {
        const string yaml =
            "corners:\n  - id: free_talk\n    title: \"フリートーク\"\n    theme: \"音楽\"\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.Null(corners[0].ThemePool); // 省略時は null（= Theme 固定）
    }

    [Fact]
    public void Corners_EmptyThemesList_ThemePoolEmptyOrNull()
    {
        // themes: [] は空プール → SelectTheme は Theme 固定に倒す（null/空を同一視）。
        const string yaml =
            "corners:\n  - id: free_talk\n    title: \"フリートーク\"\n    theme: \"音楽\"\n    themes: []\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var corners = CornersConfig.FromYaml(yaml);

        Assert.True(corners[0].ThemePool is null || corners[0].ThemePool!.Count == 0);
    }

    [Fact]
    public void Corners_MissingFallbackUri_ThrowsMissingField()
    {
        const string yaml = "corners:\n  - id: x\n    title: \"X\"\n    theme: \"音楽\"\n    dj_ids: [a]\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Corners_MissingTheme_ThrowsMissingField()
    {
        // theme 必須（Mac 一致, §4-3）。
        const string yaml =
            "corners:\n  - id: x\n    title: \"X\"\n    dj_ids: [a]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Corners_MissingDjIds_ThrowsMissingField()
    {
        // dj_ids 必須（Mac 一致, §4-3）。
        const string yaml =
            "corners:\n  - id: x\n    title: \"X\"\n    theme: \"音楽\"\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Corners_UnknownFormat_ThrowsMissingField()
    {
        // 不正な format 値は fail-fast で拒否（§4-3 起動時設定不正）。必須フィールドは揃えて format 検証に到達させる。
        const string yaml =
            "corners:\n  - id: x\n    title: \"X\"\n    theme: \"音楽\"\n    format: bogus\n" +
            "    dj_ids: [a]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    // --- W15: アーティスト特集パラメータ（format: artist_feature のときのみ構築・検証） ---

    [Fact]
    public void Corners_ArtistFeature_BuildsParams_AndMapsFormat()
    {
        const string yaml =
            "corners:\n  - id: artist_feature\n    title: \"特集\"\n    theme: \"x\"\n    format: artist_feature\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n" +
            "    intro_target_chars: 150\n    group_intro_target_chars: 300\n" +
            "    comment_target_chars: 380\n    comment_short_target_chars: 220\n" +
            "    outro_line: \"以上、{artist}特集でした。\"\n";

        var c = CornersConfig.FromYaml(yaml)[0];

        Assert.Equal(CornerFormat.ArtistFeature, c.Format);
        Assert.NotNull(c.ArtistFeatureParams);
        Assert.Equal(150, c.ArtistFeatureParams!.IntroTargetChars);
        Assert.Equal(300, c.ArtistFeatureParams.GroupIntroTargetChars);
        Assert.Equal(380, c.ArtistFeatureParams.CommentTargetChars);
        Assert.Equal(220, c.ArtistFeatureParams.CommentShortTargetChars);
        Assert.Equal("以上、{artist}特集でした。", c.ArtistFeatureParams.OutroLine);
    }

    [Fact]
    public void Corners_ArtistFeature_OmittedParams_UseDefaults()
    {
        const string yaml =
            "corners:\n  - id: artist_feature\n    title: \"特集\"\n    theme: \"x\"\n    format: artist_feature\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n";

        var c = CornersConfig.FromYaml(yaml)[0];

        Assert.NotNull(c.ArtistFeatureParams);
        Assert.Equal(200, c.ArtistFeatureParams!.IntroTargetChars);          // 既定
        Assert.Equal(240, c.ArtistFeatureParams.CommentShortTargetChars);    // 既定
        Assert.Equal("以上、{artist}特集でした。", c.ArtistFeatureParams.OutroLine);
    }

    [Fact]
    public void Corners_ArtistFeature_CommentShortNotLessThanComment_ThrowsMissingField()
    {
        const string yaml =
            "corners:\n  - id: artist_feature\n    title: \"特集\"\n    theme: \"x\"\n    format: artist_feature\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n" +
            "    comment_target_chars: 300\n    comment_short_target_chars: 300\n"; // short >= long → fail-fast

        var ex = Assert.Throws<ConfigException>(() => CornersConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Corners_NonArtistFeature_HasNullParams_NoCrossFormatValidation()
    {
        // 非 artist_feature では params を構築せず、comment_short/comment の検証もしない（Mac 一致）。
        const string yaml =
            "corners:\n  - id: free_talk\n    title: \"フリートーク\"\n    theme: \"音楽\"\n    format: free_talk\n" +
            "    dj_ids: [zundamon]\n    fallback_track_uri: \"spotify:track:fb\"\n" +
            "    comment_target_chars: 100\n    comment_short_target_chars: 999\n";

        var c = CornersConfig.FromYaml(yaml)[0];
        Assert.Null(c.ArtistFeatureParams);
    }

    // --- W-AQT: tts_backend / aquestalk_voice ---

    [Fact]
    public void Djs_DefaultTtsBackend_IsVoicevox_WhenOmitted()
    {
        // tts_backend 省略は voicevox（後方互換。既存 yaml 無改修で通る）。
        const string yaml = "djs:\n  - id: zundamon\n    name: \"ずんだもん\"\n    speaker_id: 3\n    persona: \"p\"\n";

        var dj = DjsConfig.FromYaml(yaml)[0];

        Assert.Equal("voicevox", dj.TtsBackend);
        Assert.Null(dj.AquesTalkVoice);
    }

    [Fact]
    public void Djs_AquesTalkBackend_ReadsVoice()
    {
        const string yaml =
            "djs:\n  - id: reimu\n    name: \"博麗霊夢\"\n    speaker_id: 1001\n    persona: \"p\"\n" +
            "    tts_backend: aquestalk\n    aquestalk_voice: f1\n";

        var dj = DjsConfig.FromYaml(yaml)[0];

        Assert.Equal("aquestalk", dj.TtsBackend);
        Assert.Equal("f1", dj.AquesTalkVoice);
    }

    [Fact]
    public void Djs_AquesTalkWithoutVoice_ThrowsMissingField()
    {
        const string yaml =
            "djs:\n  - id: reimu\n    name: \"博麗霊夢\"\n    speaker_id: 1001\n    persona: \"p\"\n    tts_backend: aquestalk\n";

        var ex = Assert.Throws<ConfigException>(() => DjsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Djs_UnknownTtsBackend_ThrowsMissingField()
    {
        const string yaml =
            "djs:\n  - id: x\n    name: \"X\"\n    speaker_id: 3\n    persona: \"p\"\n    tts_backend: bogus\n";

        var ex = Assert.Throws<ConfigException>(() => DjsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
