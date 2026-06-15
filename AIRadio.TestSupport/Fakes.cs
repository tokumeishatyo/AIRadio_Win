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
