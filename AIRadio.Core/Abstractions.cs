namespace AIRadio.Core;

// 外部依存はすべてこれら interface 越しにアクセスし、テストで fake 差し替え可能にする（CLAUDE.md §4-2）。
// すべての非同期 I/O は CancellationToken を引数で受ける（暗黙伝播はない。CLAUDE.md §3-1）。

/// <summary>テキスト生成（Gemini 等）。</summary>
public interface ILLMBackend
{
    Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default);
}

/// <summary>音声合成（VOICEVOX 等）。戻り値は WAV バイト列。</summary>
public interface ITTSBackend
{
    Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default);
}

/// <summary>合成済み音声（WAV）の再生。再生完了まで待機する。</summary>
public interface IAudioPlayer
{
    Task PlayAsync(byte[] wav, CancellationToken ct = default);
}

/// <summary>楽曲検索・再生可否確認（Spotify Web API）。</summary>
public interface ITrackSearcher
{
    Task<IReadOnlyList<TrackInfo>> SearchAsync(string query, int limit, CancellationToken ct = default);
    Task<bool> IsPlayableAsync(string uri, CancellationToken ct = default);
}

/// <summary>
/// アーティストの代表曲（top-tracks）取得（Spotify Web API）。アーティスト特集（W15）で実曲を確定する。
/// 重複・別バージョンの除外は呼び出し側（<see cref="ArtistFeatureEngine"/>）が行う。
/// </summary>
public interface IArtistCatalog
{
    /// <summary>指定アーティストの再生可能な代表曲を最大 <paramref name="limit"/> 曲返す。未解決は空リスト（throw しない）。</summary>
    Task<IReadOnlyList<TrackInfo>> TopTracksAsync(string artistName, int limit, CancellationToken ct = default);
}

/// <summary>Spotify 再生制御（Web API）。</summary>
public interface ISpotifyController
{
    Task PlayAsync(string uri, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task SetVolumeAsync(int percent, CancellationToken ct = default);
    Task SeekAsync(int seconds, CancellationToken ct = default);
    Task<PlayerState> PlayerStateAsync(CancellationToken ct = default);

    /// <summary>現在の曲の長さ（秒）。取得できない場合は 0。</summary>
    Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default);
}

/// <summary>外部リサーチ素材の取得（News RSS / 気象庁 等）。</summary>
public interface IResearchSource
{
    Task<string> FetchAsync(CancellationToken ct = default);
}

/// <summary>時刻と待機の抽象（テストで差し替え可能にする）。</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    Task DelayAsync(double seconds, CancellationToken ct = default);
}

/// <summary>
/// 進行ログ＋生成された会話/台本内容の出力 seam。Mac 版の print() を抽象化したもの。
/// 実装は標準出力（ConsoleRadioLog）。秘密値は呼び出し側でマスクしてから渡す（CLAUDE.md §3-3）。
/// </summary>
public interface IRadioLog
{
    void Log(string message);
}
