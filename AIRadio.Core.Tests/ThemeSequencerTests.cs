using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

public class ThemeSequencerTests
{
    private static ThemeConfig MakeTheme(string? tagline) =>
        new(
            Tagline: tagline,
            TrackUri: "spotify:track:bgm",
            IntroSeconds: 5,
            Volume: 80,
            DuckedVolume: 30,
            OutroSeconds: 3);

    private static ThemeSequencer MakeSequencer(IAudioPlayer audio, ISpotifyController spotify) =>
        new(new InMemoryTTS(), audio, spotify, new FakeClock());

    [Fact]
    public async Task OrdersEventsWithTagline()
    {
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var audio = new SpyAudioPlayer();
        var sequencer = MakeSequencer(audio, spotify);

        await sequencer.RunAsync(MakeTheme(tagline: "タグライン"), "本文", speakerId: 3);

        Assert.Equal(
            new SpotifyEvent[]
            {
                new SpotifyEvent.Play("spotify:track:bgm"),
                new SpotifyEvent.SetVolume(80), // イントロ フル音量
                new SpotifyEvent.SetVolume(30), // ダッキング
                new SpotifyEvent.Seek(57),      // 曲の残り 3 秒へ（60 - 3）
                new SpotifyEvent.SetVolume(80), // アンダック
                new SpotifyEvent.Pause(),       // 完全静寂
            },
            spotify.Events);
        Assert.Equal(2, audio.Played.Count); // tagline + announcement
    }

    [Fact]
    public async Task SkipsTaglineAudioWhenNull()
    {
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var audio = new SpyAudioPlayer();
        var sequencer = MakeSequencer(audio, spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3);

        Assert.Single(audio.Played); // announcement のみ
        Assert.Equal(new SpotifyEvent.Play("spotify:track:bgm"), spotify.Events.First());
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last());
    }

    [Fact]
    public async Task SkipsSeekWhenDurationUnknownOrShorterThanOutro()
    {
        var spotify = new FakeSpotifyController(durationSeconds: 0); // 取得不可
        var audio = new SpyAudioPlayer();
        var sequencer = MakeSequencer(audio, spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3);

        Assert.DoesNotContain(spotify.Events, e => e is SpotifyEvent.Seek); // 曲長不明ならシークしない
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last());
    }

    [Fact]
    public void ChunksAnnouncementBySentencesAndMaxChars()
    {
        // 文末で区切り、maxChars を目安に連結。
        const string text = "一文目です。二文目はもう少し長い文です。三文目！四文目？最後は句点なしの残り";
        var chunks = ThemeSequencer.ChunkAnnouncement(text, maxChars: 20);

        // 「一文目です。二文目は…」でちょうど 20 字（上限内）→ 連結。残りは次のチャンクへ。
        Assert.Equal(
            new[]
            {
                "一文目です。二文目はもう少し長い文です。",
                "三文目！四文目？最後は句点なしの残り",
            },
            chunks);

        // 1 チャンクに収まる短文は分割しない / 空白のみは空配列。
        Assert.Equal(new[] { "短い本文。" }, ThemeSequencer.ChunkAnnouncement("短い本文。"));
        Assert.Empty(ThemeSequencer.ChunkAnnouncement("  "));
    }

    [Fact]
    public async Task PlaysLongAnnouncementInChunksInOrder()
    {
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var audio = new SpyAudioPlayer();
        var sequencer = MakeSequencer(audio, spotify);

        // maxChars(120) を超える長文 → 複数チャンクで順に再生される。
        var longText = string.Concat(Enumerable.Repeat("これは長いニュース原稿のテストです。", 20)); // 18 字 × 20
        await sequencer.RunAsync(MakeTheme(tagline: null), longText, speakerId: 13);

        Assert.True(audio.Played.Count > 1);
        // InMemoryTTS は "speakerId:text" を返す → "13:" を剥がして連結すると元の原稿に戻る（順序保証）。
        var joined = string.Concat(
            audio.Played.Select(wav => Encoding.UTF8.GetString(wav)["13:".Length..]));
        Assert.Equal(longText, joined);
    }

    [Fact]
    public async Task PausesEvenWhenPlaybackFails()
    {
        var spotify = new FakeSpotifyController();
        var sequencer = MakeSequencer(new ThrowingAudioPlayer(), spotify);

        var ex = await Assert.ThrowsAsync<AudioException>(
            () => sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3));
        Assert.Equal("E-RTM-AUDIO-PLAYBACK-001", ex.Code);

        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);            // §3-1 完全静寂を保証
        Assert.Equal(new SpotifyEvent.SetVolume(80), spotify.Events.Last());  // ダッキング中の停止でも音量をフルへ復元
    }

    [Fact]
    public void ChunkAnnouncement_PreservesNewlinesButTrimsHorizontalWhitespace()
    {
        // Swift の CharacterSet.whitespaces 相当: 改行は保持、スペース/タブ/Zs（U+3000 含む）は両端トリム。
        Assert.Equal(new[] { "本文。\n" }, ThemeSequencer.ChunkAnnouncement("本文。\n")); // 末尾改行は保持
        Assert.Equal(new[] { "本文。" }, ThemeSequencer.ChunkAnnouncement("　本文。　")); // 全角空白は除去
        Assert.Equal(new[] { "本文。" }, ThemeSequencer.ChunkAnnouncement("  本文。  ")); // 半角空白は除去
    }

    [Fact]
    public async Task SeeksWithAwayFromZeroRounding()
    {
        // Swift の Double.rounded()（中点は 0 から遠い側）に一致: 180.5 → 181、シーク = 181 - 3 = 178。
        var spotify = new FakeSpotifyController(durationSeconds: 180.5);
        var sequencer = MakeSequencer(new SpyAudioPlayer(), spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3);

        Assert.Contains(new SpotifyEvent.Seek(178), spotify.Events);
    }

    [Theory]
    [InlineData(2)] // 正の短尺（OutroSeconds 3 秒より短い）
    [InlineData(3)] // ちょうど境界（duration == OutroSeconds）
    public async Task SkipsSeekWhenDurationNotLongerThanOutro(double durationSeconds)
    {
        var spotify = new FakeSpotifyController(durationSeconds: durationSeconds);
        var sequencer = MakeSequencer(new SpyAudioPlayer(), spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3);

        Assert.DoesNotContain(spotify.Events, e => e is SpotifyEvent.Seek);
        Assert.Equal(new SpotifyEvent.SetVolume(80), spotify.Events[^2]); // アンダック（フル音量）
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last());
    }

    [Fact]
    public async Task ContinuesWhenDurationFetchFails()
    {
        // 曲長取得が失敗してもシークを飛ばしてアンダック → 停止まで完遂し、例外は伝播しない（Mac の try? 相当）。
        var spotify = new DurationThrowingSpotify();
        var sequencer = MakeSequencer(new SpyAudioPlayer(), spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "本文", speakerId: 3); // throw しなければ成功

        Assert.DoesNotContain(spotify.Events, e => e is SpotifyEvent.Seek);
        Assert.Equal(new SpotifyEvent.SetVolume(80), spotify.Events[^2]); // アンダック
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last());
    }

    [Fact]
    public async Task PrefetchesNextChunkDuringCurrentPlayback()
    {
        // S11 の肝: 次チャンクの合成は現チャンク再生の「前」に始まる（再生中に裏で先行合成）。
        var log = new OrderLog();
        var sequencer = new ThemeSequencer(
            new LoggingTts(log), new LoggingAudio(log), new FakeSpotifyController(durationSeconds: 60), new FakeClock());
        // 70 字 + 。 を 2 文 → maxChars(120) で 2 チャンクに割れる。
        var text = new string('あ', 70) + "。" + new string('い', 70) + "。";

        await sequencer.RunAsync(MakeTheme(tagline: null), text, speakerId: 3);

        var entries = log.Entries.ToList();
        Assert.True(entries.Count >= 4);
        // チャンク 1（2 本目）の合成は、チャンク 0（1 本目）の再生より前に記録される。
        Assert.True(entries.IndexOf("synth:2") < entries.IndexOf("play:1"));
    }

    [Fact]
    public async Task EmptyAnnouncementPlaysBgmWithoutSpeech()
    {
        // 発話ゼロ（空白のみ）: tagline も発話もなく、BGM 演出だけが進む（else 分岐 + 発話ループ skip）。
        var spotify = new FakeSpotifyController(durationSeconds: 60);
        var audio = new SpyAudioPlayer();
        var sequencer = MakeSequencer(audio, spotify);

        await sequencer.RunAsync(MakeTheme(tagline: null), "  ", speakerId: 3);

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

    /// <summary>再生で必ず失敗する AudioPlayer（完全静寂の後始末を検証する用）。</summary>
    private sealed class ThrowingAudioPlayer : IAudioPlayer
    {
        public Task PlayAsync(byte[] wav, CancellationToken ct = default) => throw AudioException.PlaybackFailed();
    }

    /// <summary>曲長取得（CurrentTrackDurationSecondsAsync）だけが失敗する SpotifyController。イベントは記録する。</summary>
    private sealed class DurationThrowingSpotify : ISpotifyController
    {
        private readonly object _lock = new();
        private readonly List<SpotifyEvent> _events = new();

        public IReadOnlyList<SpotifyEvent> Events
        {
            get { lock (_lock) { return _events.ToList(); } }
        }

        public Task PlayAsync(string uri, CancellationToken ct = default)
        {
            lock (_lock) { _events.Add(new SpotifyEvent.Play(uri)); }
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken ct = default)
        {
            lock (_lock) { _events.Add(new SpotifyEvent.Pause()); }
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(int percent, CancellationToken ct = default)
        {
            lock (_lock) { _events.Add(new SpotifyEvent.SetVolume(percent)); }
            return Task.CompletedTask;
        }

        public Task SeekAsync(int seconds, CancellationToken ct = default)
        {
            lock (_lock) { _events.Add(new SpotifyEvent.Seek(seconds)); }
            return Task.CompletedTask;
        }

        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
            => Task.FromResult(new PlayerState(PlaybackState.Playing));

        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => throw SpotifyException.ApiFailed("duration fetch failed");
    }

    /// <summary>合成・再生の呼び出し順を 1 本のログに記録する（先行合成のタイミング検証用）。</summary>
    private sealed class OrderLog
    {
        private readonly object _lock = new();
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries
        {
            get { lock (_lock) { return _entries.ToList(); } }
        }

        public void Add(string entry)
        {
            lock (_lock) { _entries.Add(entry); }
        }
    }

    private sealed class LoggingTts : ITTSBackend
    {
        private readonly OrderLog _log;
        private int _count;

        public LoggingTts(OrderLog log) => _log = log;

        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _count);
            _log.Add($"synth:{n}");
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(text));
        }
    }

    private sealed class LoggingAudio : IAudioPlayer
    {
        private readonly OrderLog _log;
        private int _count;

        public LoggingAudio(OrderLog log) => _log = log;

        public Task PlayAsync(byte[] wav, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _count);
            _log.Add($"play:{n}");
            return Task.CompletedTask;
        }
    }
}
