using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-AQT: RoutingTTS の speaker_id 振り分けとマップ構築・一意性検証。</summary>
public class RoutingTTSTests
{
    private static DjProfile Vv(string id, int speakerId)
        => new(id, id, speakerId, "p");

    private static DjProfile Aq(string id, int speakerId, string voice)
        => new(id, id, speakerId, "p", "aquestalk", voice);

    private static AquesTalk1Synthesizer BuildSynth(FakeNativeAquesTalk1 native)
        => new(native, "native/aqk2k/aq_dic", "DEV", 100);

    [Fact]
    public async Task SpeakerInMap_RoutesToAquesTalk_WithVoiceId_NotVoicevox()
    {
        var voicevox = new RecordingTTS();
        var native = new FakeNativeAquesTalk1 { SynthResult = new byte[77] };
        using var synth = BuildSynth(native);
        var routing = new RoutingTTS(voicevox, synth, new Dictionary<int, string> { [1001] = "f1" });

        var wav = await routing.SynthesizeAsync("こんにちは", 1001);

        Assert.Equal("f1", native.LastVoiceId);     // 正しい声種で AquesTalk へ
        Assert.Equal(77, wav.Length);                // AquesTalk の出力を返す
        Assert.Equal(0, voicevox.CallCount);         // VOICEVOX は呼ばない
    }

    [Fact]
    public async Task SpeakerNotInMap_RoutesToVoicevox()
    {
        var voicevox = new RecordingTTS { Result = new byte[10] };
        var native = new FakeNativeAquesTalk1();
        using var synth = BuildSynth(native);
        var routing = new RoutingTTS(voicevox, synth, new Dictionary<int, string> { [1001] = "f1" });

        var wav = await routing.SynthesizeAsync("ニュースです", 13);

        Assert.Equal(1, voicevox.CallCount);
        Assert.Equal("ニュースです", voicevox.LastText);
        Assert.Equal(13, voicevox.LastSpeakerId);
        Assert.Equal(10, wav.Length);
        Assert.DoesNotContain("synth", native.Calls);   // AquesTalk は呼ばない（ctor の create のみ）
    }

    [Fact]
    public async Task EngineNull_MapHit_ReturnsSilent8kHzWav_WarnsOnce_NotVoicevox()
    {
        var voicevox = new RecordingTTS();
        var warnings = new List<string>();
        var routing = new RoutingTTS(
            voicevox, aquestalk: null,
            new Dictionary<int, string> { [1001] = "f1" },
            warnOnce: warnings.Add);

        var w1 = await routing.SynthesizeAsync("a", 1001);
        var w2 = await routing.SynthesizeAsync("b", 1001);

        Assert.Equal(44, w1.Length);
        Assert.Equal(8000, BitConverter.ToInt32(w1, 24));
        Assert.Equal(44, w2.Length);
        Assert.Equal(0, voicevox.CallCount);   // VOICEVOX へフォールバックしない（声種が無意味なため）
        Assert.Single(warnings);               // 警告は一度だけ
    }

    [Fact]
    public void BuildVoiceMap_BuildsAquesTalkEntriesOnly()
    {
        var map = RoutingTTS.BuildVoiceMap(new[]
        {
            Vv("zundamon", 3),
            Aq("reimu", 1001, "f1"),
            Aq("marisa", 1002, "f2"),
        });

        Assert.Equal(2, map.Count);
        Assert.Equal("f1", map[1001]);
        Assert.Equal("f2", map[1002]);
        Assert.False(map.ContainsKey(3));   // VOICEVOX DJ は入らない
    }

    [Fact]
    public void BuildVoiceMap_DuplicateAquesTalkSpeakerId_ThrowsConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() => RoutingTTS.BuildVoiceMap(new[]
        {
            Aq("reimu", 1001, "f1"),
            Aq("marisa", 1001, "f2"),   // speaker_id 重複
        }));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void BuildVoiceMap_AquesTalkCollidesWithVoicevox_ThrowsConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() => RoutingTTS.BuildVoiceMap(new[]
        {
            Vv("zundamon", 3),
            Aq("reimu", 3, "f1"),   // VOICEVOX と同じ speaker_id
        }));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void BuildVoiceMap_VoicevoxDjsSharingSpeakerId_IsAllowed()
    {
        // VOICEVOX 同士の speaker_id 重複は許容（同一バックエンドへ振り分くだけ）。
        var map = RoutingTTS.BuildVoiceMap(new[] { Vv("a", 3), Vv("b", 3) });
        Assert.Empty(map);
    }

    [Fact]
    public void BuildVoiceMap_AquesTalkWithNullVoice_ThrowsConfigException()
    {
        // 多層防御: 通常は config ローダ（DjsConfig.ResolveTtsBackend）で弾かれるが、
        // BuildVoiceMap の public 契約として空 voice も拒否することを固定（WAQT-COV-01）。
        var ex = Assert.Throws<ConfigException>(() => RoutingTTS.BuildVoiceMap(new[]
        {
            new DjProfile("reimu", "霊夢", 1001, "p", "aquestalk", null),
        }));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    /// <summary>呼び出しを記録する <see cref="ITTSBackend"/> fake（VOICEVOX 役）。</summary>
    private sealed class RecordingTTS : ITTSBackend
    {
        public int CallCount { get; private set; }
        public string? LastText { get; private set; }
        public int LastSpeakerId { get; private set; }
        public byte[] Result = new byte[8];

        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            CallCount++;
            LastText = text;
            LastSpeakerId = speakerId;
            return Task.FromResult(Result);
        }
    }
}
