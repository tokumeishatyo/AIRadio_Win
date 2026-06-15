namespace AIRadio.Core;

/// <summary>LLM へのテキスト生成リクエスト。</summary>
public sealed record LLMRequest(string Prompt, string? System = null, double Temperature = 0.7);

/// <summary>楽曲のメタ情報（Spotify 検索結果など）。</summary>
public sealed record TrackInfo(string Uri, string Title, string Artist, bool IsPlayable = true);

/// <summary>再生状態。</summary>
public enum PlaybackState
{
    Playing,
    Paused,
    Stopped,
}

/// <summary>Spotify プレイヤーの現在状態。</summary>
public sealed record PlayerState(
    PlaybackState State,
    string? TrackUri = null,
    double PositionSeconds = 0,
    double DurationSeconds = 0);
