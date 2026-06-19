using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W14 ゲストプール（<c>guests.yaml</c>）ローダ。djs と同形だが persona は任意。</summary>
public class GuestsConfigTests
{
    [Fact]
    public void Guests_FromYaml_LoadsProfiles()
    {
        const string yaml =
            "guests:\n" +
            "  - id: sora\n    name: \"九州そら\"\n    speaker_id: 16\n    persona: \"おっとり\"\n" +
            "  - id: ankomon\n    name: \"あんこもん\"\n    speaker_id: 113\n    persona: \"語尾もん\"\n";

        var guests = GuestsConfig.FromYaml(yaml);

        Assert.Equal(2, guests.Count);
        Assert.Equal(new DjProfile("sora", "九州そら", 16, "おっとり"), guests[0]);
        Assert.Equal(113, guests[1].SpeakerId);
    }

    [Fact]
    public void Guests_PersonaOptional_DefaultsEmpty()
    {
        // persona は任意（DjsConfig と異なり必須にしない。Mac 一致）。
        const string yaml = "guests:\n  - id: sora\n    name: \"九州そら\"\n    speaker_id: 16\n";

        var guests = GuestsConfig.FromYaml(yaml);

        Assert.Equal("", guests[0].Persona);
    }

    [Fact]
    public void Guests_Empty_ThrowsMissingField()
        => Assert.Throws<ConfigException>(() => GuestsConfig.FromYaml("guests: []\n"));

    [Theory]
    [InlineData("guests:\n  - name: \"x\"\n    speaker_id: 1\n")]   // id 欠落
    [InlineData("guests:\n  - id: a\n    speaker_id: 1\n")]          // name 欠落
    [InlineData("guests:\n  - id: a\n    name: \"x\"\n")]            // speaker_id 欠落
    public void Guests_MissingRequired_ThrowsMissingField(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => GuestsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    // --- W-AQT: tts_backend / aquestalk_voice（djs と共通ヘルパ） ---

    [Fact]
    public void Guests_AquesTalkBackend_ReadsVoice_AndDefaultsVoicevox()
    {
        const string yaml =
            "guests:\n" +
            "  - id: reimu\n    name: \"博麗霊夢\"\n    speaker_id: 1001\n    tts_backend: aquestalk\n    aquestalk_voice: f1\n" +
            "  - id: sora\n    name: \"九州そら\"\n    speaker_id: 16\n";

        var guests = GuestsConfig.FromYaml(yaml);

        Assert.Equal("aquestalk", guests[0].TtsBackend);
        Assert.Equal("f1", guests[0].AquesTalkVoice);
        Assert.Equal("voicevox", guests[1].TtsBackend);   // 省略は voicevox
        Assert.Null(guests[1].AquesTalkVoice);
    }

    [Fact]
    public void Guests_AquesTalkWithoutVoice_ThrowsMissingField()
    {
        const string yaml = "guests:\n  - id: reimu\n    name: \"博麗霊夢\"\n    speaker_id: 1001\n    tts_backend: aquestalk\n";

        var ex = Assert.Throws<ConfigException>(() => GuestsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
