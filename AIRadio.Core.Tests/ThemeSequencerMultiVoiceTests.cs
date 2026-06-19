using System.Buffers.Binary;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W-OP 多声 OP（<see cref="ThemeSequencer"/> の多声オーバーロード）の検証。単独 step / 同時発話（PCM ミックス）/
/// 形式不一致フォールバック / prefetch / 空 / cancel / tagline を fake 注入で決定論的に検証する。
/// </summary>
public class ThemeSequencerMultiVoiceTests
{
    private const int Reimu = 1001;
    private const int Marisa = 1002;

    private static ThemeConfig MakeTheme(string? tagline) =>
        new(Tagline: tagline, TrackUri: "spotify:track:bgm", IntroSeconds: 5, Volume: 80, DuckedVolume: 30, OutroSeconds: 3);

    /// <summary>16bit/mono の WAV を short サンプルから組み立てる。</summary>
    private static byte[] WavOf(int sampleRate, params short[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
        }
        return Wav.BuildPcmWav(sampleRate, 1, 16, pcm);
    }

    private static short[] SamplesOf(byte[] wav)
    {
        var pcm = Wav.Parse(wav).Samples;
        var result = new short[pcm.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
        }
        return result;
    }

    private static SpokenStep Single(int speakerId, string text) =>
        new(new[] { new VoiceLine(speakerId, text) });

    private static SpokenStep Together(params (int SpeakerId, string Text)[] voices) =>
        new(voices.Select(v => new VoiceLine(v.SpeakerId, v.Text)).ToList());

    [Fact]
    public async Task SingleVoiceStep_PlaysOncePerStep_WithBgmScaffold()
    {
        var tts = new RecordingWavTts(_ => WavOf(8000, 100));
        var audio = new SpyAudioPlayer();
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var seq = new ThemeSequencer(tts, audio, spotify, new FakeClock());

        await seq.RunAsync(MakeTheme(tagline: null), Reimu, new[] { Single(Reimu, "ゆっくり霊夢です") });

        Assert.Single(audio.Played);
        Assert.Equal(
            new SpotifyEvent[]
            {
                new SpotifyEvent.Play("spotify:track:bgm"),
                new SpotifyEvent.SetVolume(80), // イントロ フル
                new SpotifyEvent.SetVolume(30), // ダッキング
                new SpotifyEvent.Seek(57),      // 60 - 3
                new SpotifyEvent.SetVolume(80), // アンダック
                new SpotifyEvent.Pause(),
            },
            spotify.Events);
    }

    [Fact]
    public async Task SimultaneousStep_MixesVoicesIntoOnePlay()
    {
        var tts = new RecordingWavTts(spk => spk == Reimu ? WavOf(8000, 100) : WavOf(8000, 200));
        var audio = new SpyAudioPlayer();
        var seq = new ThemeSequencer(tts, audio, new FakeSpotifyController(durationSeconds: 60), new FakeClock());

        await seq.RunAsync(MakeTheme(tagline: null), Reimu,
            new[] { Together((Reimu, "ゆっくりしていってね"), (Marisa, "ゆっくりしていってね")) });

        Assert.Equal(2, tts.Calls.Count);              // 各声を 1 回ずつ合成
        Assert.Single(audio.Played);                   // ミックスして 1 WAV で再生
        Assert.Equal(new short[] { 300 }, SamplesOf(audio.Played[0])); // 100 + 200
    }

    [Fact]
    public async Task FormatMismatch_FallsBackToSequential_WithWarnOnce()
    {
        var warns = new List<string>();
        var tts = new RecordingWavTts(spk => spk == Reimu ? WavOf(8000, 100) : WavOf(16000, 200)); // 8kHz と 16kHz
        var audio = new SpyAudioPlayer();
        var seq = new ThemeSequencer(tts, audio, new FakeSpotifyController(durationSeconds: 60), new FakeClock(), warns.Add);

        await seq.RunAsync(MakeTheme(tagline: null), Reimu,
            new[] { Together((Reimu, "x"), (Marisa, "y")) });

        Assert.Equal(2, audio.Played.Count); // 順次再生にフォールバック
        Assert.Single(warns);                // 不一致グループにつき 1 回
    }

    [Fact]
    public async Task TwoMismatchedSteps_WarnsTwice()
    {
        var warns = new List<string>();
        var tts = new RecordingWavTts(spk => spk == Reimu ? WavOf(8000, 1) : WavOf(16000, 2));
        var audio = new SpyAudioPlayer();
        var seq = new ThemeSequencer(tts, audio, new FakeSpotifyController(durationSeconds: 60), new FakeClock(), warns.Add);

        await seq.RunAsync(MakeTheme(tagline: null), Reimu,
            new[]
            {
                Together((Reimu, "a"), (Marisa, "b")),
                Together((Reimu, "c"), (Marisa, "d")),
            });

        Assert.Equal(2, warns.Count);        // step ごとに 1 回
        Assert.Equal(4, audio.Played.Count); // 各 step 2 声を順次
    }

    [Fact]
    public async Task EmptySteps_PlaysBgmScaffoldOnly()
    {
        var audio = new SpyAudioPlayer();
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var seq = new ThemeSequencer(new RecordingWavTts(_ => WavOf(8000, 1)), audio, spotify, new FakeClock());

        await seq.RunAsync(MakeTheme(tagline: null), Reimu, Array.Empty<SpokenStep>());

        Assert.Empty(audio.Played);
        Assert.Equal(
            new SpotifyEvent[]
            {
                new SpotifyEvent.Play("spotify:track:bgm"),
                new SpotifyEvent.SetVolume(80),
                new SpotifyEvent.SetVolume(30),
                new SpotifyEvent.Seek(57),
                new SpotifyEvent.SetVolume(80),
                new SpotifyEvent.Pause(),
            },
            spotify.Events);
    }

    [Fact]
    public async Task TaglineIsSpokenByTaglineSpeaker_BeforeSteps()
    {
        var tts = new RecordingWavTts(_ => WavOf(8000, 1));
        var audio = new SpyAudioPlayer();
        var seq = new ThemeSequencer(tts, audio, new FakeSpotifyController(durationSeconds: 60), new FakeClock());

        await seq.RunAsync(MakeTheme(tagline: "ゆっくり ケイラボAIラジオ"), Reimu, new[] { Single(Marisa, "ゆっくり魔理沙だぜ") });

        Assert.Equal(2, audio.Played.Count);                       // tagline + step
        Assert.Equal(("ゆっくり ケイラボAIラジオ", Reimu), tts.Calls[0]); // tagline は taglineSpeaker
        Assert.Equal(("ゆっくり魔理沙だぜ", Marisa), tts.Calls[1]);     // step は各話者
    }

    [Fact]
    public async Task PrefetchesNextStepDuringCurrentPlayback()
    {
        var log = new List<string>();
        var seq = new ThemeSequencer(
            new OrderTts(log, _ => WavOf(8000, 1)), new OrderAudio(log),
            new FakeSpotifyController(durationSeconds: 60), new FakeClock());

        await seq.RunAsync(MakeTheme(tagline: null), Reimu,
            new[] { Single(Reimu, "a"), Single(Marisa, "b") });

        // step[1] の合成は step[0] の再生より前に始まる（無音排除・S11 と同型）。
        Assert.True(log.IndexOf("synth:2") < log.IndexOf("play:1"));
    }

    [Fact]
    public async Task CancelDuringSynthesis_ThrowsAndPauses()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var seq = new ThemeSequencer(new ThrowOnCancelTts(), new SpyAudioPlayer(), spotify, new FakeClock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => seq.RunAsync(MakeTheme(tagline: null), Reimu, new[] { Together((Reimu, "a"), (Marisa, "b")) }, cts.Token));

        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // §3-1 完全静寂
    }

    [Fact]
    public async Task CancelDuringSimultaneousSynthesis_ThrowsAndPauses_NoPartialMix()
    {
        // 合成中（Task.WhenAll 進行中）に cancel → 両声がキャンセル・中途ミックスなし・fail-safe pause。
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var audio = new SpyAudioPlayer();
        var seq = new ThemeSequencer(new SlowTts(), audio, spotify, new FakeClock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => seq.RunAsync(MakeTheme(tagline: null), Reimu, new[] { Together((Reimu, "a"), (Marisa, "b")) }, cts.Token));

        Assert.Empty(audio.Played);                                // 中途ミックス・再生なし
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // §3-1 完全静寂
    }

    // --- fakes ---

    private sealed class RecordingWavTts : ITTSBackend
    {
        private readonly Func<int, byte[]> _wav;
        public readonly List<(string Text, int SpeakerId)> Calls = new();

        public RecordingWavTts(Func<int, byte[]> wav) => _wav = wav;

        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            lock (Calls) { Calls.Add((text, speakerId)); }
            return Task.FromResult(_wav(speakerId));
        }
    }

    private sealed class OrderTts : ITTSBackend
    {
        private readonly List<string> _log;
        private readonly Func<int, byte[]> _wav;
        private int _n;

        public OrderTts(List<string> log, Func<int, byte[]> wav) { _log = log; _wav = wav; }

        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _n);
            lock (_log) { _log.Add($"synth:{n}"); }
            return Task.FromResult(_wav(speakerId));
        }
    }

    private sealed class OrderAudio : IAudioPlayer
    {
        private readonly List<string> _log;
        private int _n;

        public OrderAudio(List<string> log) => _log = log;

        public Task PlayAsync(byte[] wav, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _n);
            lock (_log) { _log.Add($"play:{n}"); }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowOnCancelTts : ITTSBackend
    {
        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WavOf(8000, 1));
        }
    }

    /// <summary>合成が ct のキャンセルまで完了しない（合成中キャンセルの再現用）。</summary>
    private sealed class SlowTts : ITTSBackend
    {
        public async Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return WavOf(8000, 1);
        }
    }
}
