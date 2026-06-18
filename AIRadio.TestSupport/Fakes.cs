using AIRadio.Core;

namespace AIRadio.TestSupport;

// 外部依存の fake 一式（テストで Core を完全に単体テストするための差し替え。CLAUDE.md §4-4）。

// MARK: ステートレス fake

/// <summary>プロンプトをそのままエコーする LLM。</summary>
public sealed class EchoLLM : ILLMBackend
{
    public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        => Task.FromResult("echo: " + request.Prompt);
}

/// <summary>用意した応答を順番に返す LLM（記録 fake）。応答が尽きたら EmptyResponse を投げる。</summary>
public sealed class ScriptedLLM : ILLMBackend
{
    private readonly object _lock = new();
    private readonly Queue<string> _responses;
    private readonly List<LLMRequest> _requests = new();

    public ScriptedLLM(params string[] responses) => _responses = new Queue<string>(responses);

    public IReadOnlyList<LLMRequest> Requests
    {
        get { lock (_lock) { return _requests.ToList(); } }
    }

    public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _requests.Add(request);
            if (_responses.Count == 0)
            {
                throw LlmException.EmptyResponse();
            }
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

/// <summary>テキストと話者 ID から決定論的なダミー WAV データを返す TTS。</summary>
public sealed class InMemoryTTS : ITTSBackend
{
    public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        => Task.FromResult(System.Text.Encoding.UTF8.GetBytes($"{speakerId}:{text}"));
}

/// <summary>固定時刻・即時 delay の Clock。</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; }

    public FakeClock(DateTimeOffset? now = null) => Now = now ?? DateTimeOffset.UnixEpoch;

    public Task DelayAsync(double seconds, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>固定ペイロードを返す ResearchSource。</summary>
public sealed class FakeResearchSource : IResearchSource
{
    private readonly string _payload;

    public FakeResearchSource(string payload) => _payload = payload;

    public Task<string> FetchAsync(CancellationToken ct = default) => Task.FromResult(_payload);
}

// MARK: 記録 fake（呼び出しを記録。lock でスレッド安全に）

/// <summary>再生された WAV を記録する AudioPlayer。</summary>
public sealed class SpyAudioPlayer : IAudioPlayer
{
    private readonly object _lock = new();
    private readonly List<byte[]> _played = new();

    public IReadOnlyList<byte[]> Played
    {
        get { lock (_lock) { return _played.ToList(); } }
    }

    public Task PlayAsync(byte[] wav, CancellationToken ct = default)
    {
        lock (_lock) { _played.Add(wav); }
        return Task.CompletedTask;
    }
}

/// <summary>検索クエリを記録し、設定済み結果を返す TrackSearcher。</summary>
public sealed class FakeTrackSearcher : ITrackSearcher
{
    private readonly object _lock = new();
    private readonly List<TrackInfo> _results;
    private readonly List<string> _queries = new();

    public FakeTrackSearcher(IEnumerable<TrackInfo>? results = null)
        => _results = results?.ToList() ?? new List<TrackInfo>();

    public IReadOnlyList<string> Queries
    {
        get { lock (_lock) { return _queries.ToList(); } }
    }

    public Task<IReadOnlyList<TrackInfo>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _queries.Add(query);
            IReadOnlyList<TrackInfo> page = _results.Take(limit).ToList();
            return Task.FromResult(page);
        }
    }

    public Task<bool> IsPlayableAsync(string uri, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var match = _results.FirstOrDefault(t => t.Uri == uri);
            return Task.FromResult(match?.IsPlayable ?? false);
        }
    }
}

/// <summary>Spotify 制御の呼び出し（種類と引数）。記録 fake の検証に使う。</summary>
public abstract record SpotifyEvent
{
    public sealed record Play(string Uri) : SpotifyEvent;

    public sealed record Pause : SpotifyEvent;

    public sealed record SetVolume(int Percent) : SpotifyEvent;

    public sealed record Seek(int Seconds) : SpotifyEvent;
}

/// <summary>Spotify 制御の呼び出し順を記録する SpotifyController。</summary>
public sealed class FakeSpotifyController : ISpotifyController
{
    private readonly object _lock = new();
    private readonly List<SpotifyEvent> _events = new();
    private readonly double _duration;
    private PlayerState _state;

    public FakeSpotifyController(PlayerState? state = null, double durationSeconds = 0)
    {
        _state = state ?? new PlayerState(PlaybackState.Stopped);
        _duration = durationSeconds;
    }

    public IReadOnlyList<SpotifyEvent> Events
    {
        get { lock (_lock) { return _events.ToList(); } }
    }

    public Task PlayAsync(string uri, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _events.Add(new SpotifyEvent.Play(uri));
            _state = new PlayerState(PlaybackState.Playing, uri, 0, _duration);
        }
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
    {
        lock (_lock) { return Task.FromResult(_state); }
    }

    public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
        => Task.FromResult(_duration);
}

/// <summary>アーティスト名→代表曲を返す <see cref="IArtistCatalog"/> の fake（W15）。問い合わせた名前を記録する。</summary>
public sealed class FakeArtistCatalog : IArtistCatalog
{
    private readonly object _lock = new();
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TrackInfo>> _byArtist;
    private readonly List<string> _requested = new();

    public FakeArtistCatalog(IReadOnlyDictionary<string, IReadOnlyList<TrackInfo>>? byArtist = null)
        => _byArtist = byArtist ?? new Dictionary<string, IReadOnlyList<TrackInfo>>();

    public IReadOnlyList<string> Requested
    {
        get { lock (_lock) { return _requested.ToList(); } }
    }

    public Task<IReadOnlyList<TrackInfo>> TopTracksAsync(string artistName, int limit, CancellationToken ct = default)
    {
        lock (_lock) { _requested.Add(artistName); }
        var tracks = _byArtist.TryGetValue(artistName, out var t) ? t : Array.Empty<TrackInfo>();
        IReadOnlyList<TrackInfo> page = tracks.Take(limit).ToList();
        return Task.FromResult(page);
    }
}

/// <summary>ログメッセージを記録する IRadioLog（テスト用）。</summary>
public sealed class CollectingRadioLog : IRadioLog
{
    private readonly object _lock = new();
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages
    {
        get { lock (_lock) { return _messages.ToList(); } }
    }

    public void Log(string message)
    {
        lock (_lock) { _messages.Add(message); }
    }
}

/// <summary>
/// 仮想時間で再生位置が進む Spotify + Clock のシミュレータ（フル再生の終端検知テスト用）。
/// <see cref="DelayAsync"/> が仮想時間を進め、<see cref="PlayerStateAsync"/> は再生開始からの経過 = 再生位置を返す。
/// <see cref="ISpotifyController"/> と <see cref="IClock"/> の両方に同じインスタンスを渡して使う。
/// </summary>
public sealed class PlaybackSimulator : ISpotifyController, IClock
{
    private readonly object _lock = new();
    private readonly IReadOnlyDictionary<string, double> _durations;
    // false = スナップショットに曲長を含めない実装（AppleScript 等の互換パス）を再現。
    private readonly bool _snapshotIncludesDuration;
    // currentTrackDurationSeconds()（別問い合わせ）が返す値の上書き。stale な曲長の注入用。
    private readonly double? _legacyDurationOverride;
    // playerState() が最初に返す stale スナップショット列（切替直後の前曲応答などの再現）。
    private readonly Queue<PlayerState> _staleSnapshots;
    // playerState() の n 回目（1 始まり）の応答を stale に差し替える（再生途中の stale 混入の再現）。
    private readonly IReadOnlyDictionary<int, PlayerState> _staleByCall;
    private int _stateCalls;
    private double _virtualTime;
    private string? _currentUri;
    private double _startedAt;
    private bool _paused = true;
    private readonly List<double> _sleeps = new();
    private readonly List<SpotifyEvent> _events = new();

    public PlaybackSimulator(
        IReadOnlyDictionary<string, double> durations,
        bool snapshotIncludesDuration = true,
        double? legacyDurationOverride = null,
        IEnumerable<PlayerState>? staleSnapshots = null,
        IReadOnlyDictionary<int, PlayerState>? staleByCall = null)
    {
        _durations = durations;
        _snapshotIncludesDuration = snapshotIncludesDuration;
        _legacyDurationOverride = legacyDurationOverride;
        _staleSnapshots = new Queue<PlayerState>(staleSnapshots ?? Enumerable.Empty<PlayerState>());
        _staleByCall = staleByCall ?? new Dictionary<int, PlayerState>();
    }

    public IReadOnlyList<double> Sleeps
    {
        get { lock (_lock) { return _sleeps.ToList(); } }
    }

    public IReadOnlyList<SpotifyEvent> Events
    {
        get { lock (_lock) { return _events.ToList(); } }
    }

    /// <summary>現在の仮想再生位置（テストの検証用）。</summary>
    public double CurrentPositionSeconds
    {
        get { lock (_lock) { return PositionLocked(); } }
    }

    // MARK: IClock

    public DateTimeOffset Now
    {
        get { lock (_lock) { return DateTimeOffset.UnixEpoch.AddSeconds(_virtualTime); } }
    }

    public Task DelayAsync(double seconds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sleeps.Add(seconds);
            _virtualTime += Math.Max(seconds, 0);
        }
        return Task.CompletedTask;
    }

    // MARK: ISpotifyController

    public Task PlayAsync(string uri, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _events.Add(new SpotifyEvent.Play(uri));
            _currentUri = uri;
            _startedAt = _virtualTime;
            _paused = false;
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        lock (_lock) { _events.Add(new SpotifyEvent.Pause()); _paused = true; }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int percent, CancellationToken ct = default)
    {
        lock (_lock) { _events.Add(new SpotifyEvent.SetVolume(percent)); }
        return Task.CompletedTask;
    }

    public Task SeekAsync(int seconds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _events.Add(new SpotifyEvent.Seek(seconds));
            _startedAt = _virtualTime - seconds;
        }
        return Task.CompletedTask;
    }

    public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _stateCalls++;
            if (_staleByCall.TryGetValue(_stateCalls, out var stale))
            {
                return Task.FromResult(stale);
            }
            if (_staleSnapshots.Count > 0)
            {
                return Task.FromResult(_staleSnapshots.Dequeue());
            }
            if (_currentUri is not string uri || _paused)
            {
                return Task.FromResult(new PlayerState(
                    _currentUri is null ? PlaybackState.Stopped : PlaybackState.Paused, _currentUri));
            }
            var duration = _durations.TryGetValue(uri, out var d) ? d : 0;
            var position = PositionLocked();
            // 曲の終端に達したら自然停止（paused）として観測される。
            if (duration > 0 && position >= duration)
            {
                return Task.FromResult(new PlayerState(
                    PlaybackState.Paused, uri, duration, _snapshotIncludesDuration ? duration : 0));
            }
            return Task.FromResult(new PlayerState(
                PlaybackState.Playing, uri, position, _snapshotIncludesDuration ? duration : 0));
        }
    }

    public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_legacyDurationOverride is double over)
            {
                return Task.FromResult(over);
            }
            if (_currentUri is not string uri)
            {
                return Task.FromResult(0.0);
            }
            return Task.FromResult(_durations.TryGetValue(uri, out var d) ? d : 0);
        }
    }

    private double PositionLocked()
    {
        if (_currentUri is not string uri)
        {
            return 0;
        }
        var elapsed = _virtualTime - _startedAt;
        var duration = _durations.TryGetValue(uri, out var d) ? d : 0;
        return duration > 0 ? Math.Min(elapsed, duration) : elapsed;
    }
}
