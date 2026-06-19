using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class TtsConfigTests
{
    [Fact]
    public void FromYaml_ParsesFields_AndClampsNumbers()
    {
        var yaml =
            "voicevox:\n" +
            "  endpoint: \"http://127.0.0.1:50021/\"\n" +
            "  credit: \"VOICEVOX:{speaker}\"\n" +
            "playback_volume: 1.5\n" +
            "speed_scale: 3.0\n";

        var cfg = TtsConfig.FromYaml(yaml);

        Assert.Equal("http://127.0.0.1:50021/", cfg.Endpoint);
        Assert.Equal("VOICEVOX:{speaker}", cfg.Credit);
        Assert.Equal(1.0, cfg.PlaybackVolume); // 0.0–1.0 にクランプ
        Assert.Equal(2.0, cfg.SpeedScale);     // 0.5–2.0 にクランプ
    }

    [Fact]
    public void FromYaml_AppliesDefaults_WhenOptionalMissing()
    {
        var yaml =
            "voicevox:\n" +
            "  endpoint: \"http://x/\"\n";

        var cfg = TtsConfig.FromYaml(yaml);

        Assert.Equal("", cfg.Credit);
        Assert.Equal(1.0, cfg.PlaybackVolume);
        Assert.Equal(1.0, cfg.SpeedScale);
        Assert.Null(cfg.VoicevoxExePath);   // 未設定は null（後方互換・W-Win）
    }

    [Fact]
    public void FromYaml_ReadsVoicevoxExePath_WhenPresent()
    {
        var yaml =
            "voicevox:\n" +
            "  endpoint: \"http://x/\"\n" +
            "  exe_path: \"C:\\\\Programs\\\\VOICEVOX\\\\VOICEVOX.exe\"\n";

        var cfg = TtsConfig.FromYaml(yaml);

        Assert.Equal("C:\\Programs\\VOICEVOX\\VOICEVOX.exe", cfg.VoicevoxExePath);
    }

    [Fact]
    public void FromYaml_MissingEndpoint_ThrowsConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() => TtsConfig.FromYaml("playback_volume: 1.0\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    // --- W-AQT: aquestalk: 節 ---

    [Fact]
    public void FromYaml_ReadsAquesTalkSection_WhenPresent()
    {
        var yaml =
            "voicevox:\n  endpoint: \"http://x/\"\n" +
            "aquestalk:\n  voices_dir: \"native/aqtk1\"\n  dict_dir: \"native/aqk2k\"\n  speed: 130\n";

        var cfg = TtsConfig.FromYaml(yaml);

        Assert.Equal("native/aqtk1", cfg.AquesTalkVoicesDir);
        Assert.Equal("native/aqk2k", cfg.AquesTalkDictDir);
        Assert.Equal(130, cfg.AquesTalkSpeed);
    }

    [Fact]
    public void FromYaml_AquesTalkDefaults_WhenSectionMissing()
    {
        var cfg = TtsConfig.FromYaml("voicevox:\n  endpoint: \"http://x/\"\n");

        Assert.Equal("native/aqtk1", cfg.AquesTalkVoicesDir);
        Assert.Equal("native/aqk2k", cfg.AquesTalkDictDir);
        Assert.Equal(100, cfg.AquesTalkSpeed);   // 既定 100
    }

    [Fact]
    public void FromYaml_AquesTalkSpeed_ClampedTo50_300()
    {
        var lo = TtsConfig.FromYaml("voicevox:\n  endpoint: \"http://x/\"\naquestalk:\n  speed: 10\n");
        var hi = TtsConfig.FromYaml("voicevox:\n  endpoint: \"http://x/\"\naquestalk:\n  speed: 999\n");

        Assert.Equal(50, lo.AquesTalkSpeed);
        Assert.Equal(300, hi.AquesTalkSpeed);
    }
}
