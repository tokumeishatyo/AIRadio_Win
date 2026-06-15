using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

// WaitForTrackToFinish の終端検知（S10 fix + S12 stale 耐性強化）の専用テスト。
// PlaybackSimulator は ISpotifyController と IClock を兼ね、DelayAsync で仮想再生位置が進む。
public class WaitForTrackToFinishTests
{
    private const string Song = "spotify:track:SONG";
    private const string OpTheme = "spotify:track:OP-THEME";

    [Fact]
    public async Task WaitsUntilNaturalEnd()
    {
        // 正常系: チャンク寝で終端 5 秒手前まで → ポーリングで実終端を検知して戻る。
        var simulator = new PlaybackSimulator(new Dictionary<string, double> { [Song] = 355 });
        await simulator.PlayAsync(Song);

        var reason = await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.True(simulator.CurrentPositionSeconds >= 350); // 終端近くまで見届けた
        Assert.All(simulator.Sleeps, s => Assert.True(s <= 30)); // まとめ寝は recheck(30s) 以下のチャンクのみ
        Assert.Equal(TrackFinishReason.ReachedEnd, reason);
    }

    [Fact]
    public async Task TransientStaleSnapshotMidPlaybackDoesNotCut()
    {
        // S12 fix-2 回帰: 再生途中のポーリングに stale な前曲スナップショットが紛れても切らない。
        var stale = new PlayerState(PlaybackState.Paused, OpTheme, 183, 183);
        var simulator = new PlaybackSimulator(
            new Dictionary<string, double> { [Song] = 355 },
            staleByCall: new Dictionary<int, PlayerState> { [3] = stale });
        await simulator.PlayAsync(Song);

        var reason = await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.True(simulator.CurrentPositionSeconds >= 350); // 途中で切らず最後まで
        Assert.Equal(TrackFinishReason.ReachedEnd, reason);
    }

    [Fact]
    public async Task ConfirmedTrackChangeReturnsTrackChanged()
    {
        // 本当に別トラックへ遷移したときは 2 回連続の観測で trackChanged を返す。
        var other = new PlayerState(PlaybackState.Playing, "spotify:track:OTHER", 10, 200);
        var simulator = new PlaybackSimulator(
            new Dictionary<string, double> { [Song] = 355 },
            staleByCall: new Dictionary<int, PlayerState> { [3] = other, [4] = other });
        await simulator.PlayAsync(Song);

        var reason = await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.Equal(TrackFinishReason.TrackChanged, reason);
        Assert.True(simulator.CurrentPositionSeconds < 350); // 遷移を確認したら粘らず戻る
    }

    [Fact]
    public async Task StaleLegacyDurationDoesNotCutTrackShort()
    {
        // S12 回帰: 別問い合わせの曲長が前の曲（183 秒）でも、同一スナップショットの曲長で最後まで待つ。
        var simulator = new PlaybackSimulator(
            new Dictionary<string, double> { [Song] = 355 },
            legacyDurationOverride: 183); // 別問い合わせは常に stale な前曲の曲長を返す
        await simulator.PlayAsync(Song);

        await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.True(simulator.CurrentPositionSeconds >= 350); // 183 秒で切らない
    }

    [Fact]
    public async Task StaleFirstSnapshotIsIgnored()
    {
        // 切替直後の stale スナップショット（前曲・paused）はスキップして待ち続ける。
        var simulator = new PlaybackSimulator(
            new Dictionary<string, double> { [Song] = 200 },
            staleSnapshots: new[] { new PlayerState(PlaybackState.Paused, OpTheme, 0) });
        await simulator.PlayAsync(Song);

        await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.True(simulator.CurrentPositionSeconds >= 195);
    }

    [Fact]
    public async Task FallsBackToLegacyDurationWhenSnapshotLacksIt()
    {
        // スナップショットに曲長を持たない実装（AppleScript 等）は別問い合わせに倒して待つ。
        var simulator = new PlaybackSimulator(
            new Dictionary<string, double> { [Song] = 200 },
            snapshotIncludesDuration: false);
        await simulator.PlayAsync(Song);

        await simulator.WaitForTrackToFinishAsync(Song, simulator);

        Assert.True(simulator.CurrentPositionSeconds >= 195);
    }

    [Fact]
    public async Task RechecksTransientPauseBeforeGivingUp()
    {
        // チャンク読み直しで「停止」が見えても 1 拍おいて再確認する（stale の paused で早切りしない）。
        var spotify = new FlakySpotify();
        var clock = new SleepRecorder();

        await spotify.WaitForTrackToFinishAsync(Song, clock);

        // stale な paused（2 回目の応答）で即 return せず、その後も待ち続けている
        // （チャンク寝が複数回 = 30 秒級の sleep が 2 回以上）。
        Assert.True(clock.Sleeps.Count(s => s > 1) >= 2);
    }

    [Fact]
    public async Task PauseIgnoringCancellation_PausesThenRestoresVolume()
    {
        // §3-1 完全静寂: 後始末 pause はキャンセル非継承（CancellationToken.None）で送り、音量を戻す。
        var spy = new CleanupSpy();

        await spy.PauseIgnoringCancellationAsync(restoringVolume: 70);

        Assert.Equal(
            new SpotifyEvent[] { new SpotifyEvent.Pause(), new SpotifyEvent.SetVolume(70) },
            spy.Events);
        Assert.False(spy.PauseTokenCanBeCanceled); // CancellationToken.None で送られた
    }

    [Fact]
    public async Task PauseIgnoringCancellation_NullVolume_OnlyPauses()
    {
        var spy = new CleanupSpy();

        await spy.PauseIgnoringCancellationAsync();

        Assert.Equal(new SpotifyEvent[] { new SpotifyEvent.Pause() }, spy.Events);
    }

    [Fact]
    public async Task PauseIgnoringCancellation_SwallowsPauseError()
    {
        // pause が例外を投げても後始末はベストエフォート（伝播させない・音量復元はスキップ）。
        var spy = new CleanupSpy(throwOnPause: true);

        await spy.PauseIgnoringCancellationAsync(restoringVolume: 70); // throw しなければ成功

        Assert.Equal(new SpotifyEvent[] { new SpotifyEvent.Pause() }, spy.Events);
    }

    [Fact]
    public async Task ReturnsPositionStalled_WhenPositionFreezesNearEnd()
    {
        // 曲が終わっても is_playing=true を返し続ける Spotify の癖: 終端付近で位置が停滞したら抜ける。
        var spotify = new FrozenPlayback(Song, position: 196, duration: 200);

        var reason = await spotify.WaitForTrackToFinishAsync(Song, new FakeClock());

        Assert.Equal(TrackFinishReason.PositionStalled, reason);
    }

    [Fact]
    public async Task ReturnsTimedOut_WhenPositionNeverAdvances()
    {
        // リピート再生・位置が進まない異常系は budget 安全弁で打ち切る（無限待ち防止）。
        var spotify = new FrozenPlayback(Song, position: 100, duration: 200);

        var reason = await spotify.WaitForTrackToFinishAsync(Song, new FakeClock());

        Assert.Equal(TrackFinishReason.TimedOut, reason);
    }

    /// <summary>途中のポーリングで一度だけ stale な paused を返し、再確認では再生中が返る controller。</summary>
    private sealed class FlakySpotify : ISpotifyController
    {
        private readonly object _lock = new();
        private int _calls;
        private double _position;

        public Task PlayAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SeekAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default) => Task.FromResult(100.0);

        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                _calls++;
                _position += 30; // 呼び出しごとに約 1 チャンクぶん進んだ体
                if (_calls == 2) // 最初のチャンク後の読み直しだけ stale な paused
                {
                    return Task.FromResult(new PlayerState(PlaybackState.Paused, Song, 0));
                }
                var pos = Math.Min(_position, 100);
                return Task.FromResult(new PlayerState(
                    pos >= 100 ? PlaybackState.Paused : PlaybackState.Playing, Song, pos, 100));
            }
        }
    }

    /// <summary>sleep を記録するだけの Clock（仮想時間は進めない）。</summary>
    private sealed class SleepRecorder : IClock
    {
        private readonly object _lock = new();
        private readonly List<double> _sleeps = new();

        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;

        public IReadOnlyList<double> Sleeps
        {
            get { lock (_lock) { return _sleeps.ToList(); } }
        }

        public Task DelayAsync(double seconds, CancellationToken ct = default)
        {
            lock (_lock) { _sleeps.Add(seconds); }
            return Task.CompletedTask;
        }
    }

    /// <summary>pause / volume の後始末呼び出しを記録する controller（pause の CancellationToken も観測）。</summary>
    private sealed class CleanupSpy : ISpotifyController
    {
        private readonly bool _throwOnPause;
        private readonly object _lock = new();
        private readonly List<SpotifyEvent> _events = new();

        public CleanupSpy(bool throwOnPause = false) => _throwOnPause = throwOnPause;

        public IReadOnlyList<SpotifyEvent> Events
        {
            get { lock (_lock) { return _events.ToList(); } }
        }

        public bool PauseTokenCanBeCanceled { get; private set; }

        public Task PlayAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

        public Task PauseAsync(CancellationToken ct = default)
        {
            PauseTokenCanBeCanceled = ct.CanBeCanceled;
            lock (_lock) { _events.Add(new SpotifyEvent.Pause()); }
            if (_throwOnPause)
            {
                throw new InvalidOperationException("pause failed");
            }
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(int percent, CancellationToken ct = default)
        {
            lock (_lock) { _events.Add(new SpotifyEvent.SetVolume(percent)); }
            return Task.CompletedTask;
        }

        public Task SeekAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
            => Task.FromResult(new PlayerState(PlaybackState.Stopped));

        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => Task.FromResult(0.0);
    }

    /// <summary>位置が一切進まない再生（リピート / 終端で is_playing=true のまま停滞する状況の再現）。</summary>
    private sealed class FrozenPlayback : ISpotifyController
    {
        private readonly string _uri;
        private readonly double _position;
        private readonly double _duration;

        public FrozenPlayback(string uri, double position, double duration)
        {
            _uri = uri;
            _position = position;
            _duration = duration;
        }

        public Task PlayAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SeekAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
            => Task.FromResult(new PlayerState(PlaybackState.Playing, _uri, _position, _duration));

        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => Task.FromResult(_duration);
    }
}
