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
    }

    [Fact]
    public void FromYaml_MissingEndpoint_ThrowsConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() => TtsConfig.FromYaml("playback_volume: 1.0\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
