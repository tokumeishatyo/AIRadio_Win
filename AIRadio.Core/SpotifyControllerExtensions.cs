namespace AIRadio.Core;

/// <summary><see cref="WaitForTrackToFinishExtensions.WaitForTrackToFinishAsync"/> の復帰理由（診断ログ用）。</summary>
public enum TrackFinishReason
{
    /// <summary>再生停止を 2 回連続で確認（曲の自然終了 or 外部からの停止）。</summary>
    Stopped,

    /// <summary>別トラックへの遷移を 2 回連続で確認。</summary>
    TrackChanged,

    /// <summary>再生位置が終端に到達。</summary>
    ReachedEnd,

    /// <summary>終端付近で位置が停滞（曲は実質終わっている）。</summary>
    PositionStalled,

    /// <summary>安全弁による打ち切り（待ち時間の上限到達。位置が進まない・切替未確認等の異常系）。</summary>
    TimedOut,
}

/// <summary>
/// <see cref="ISpotifyController"/> の拡張。Mac 版 protocol 拡張（Protocols.swift の
/// pauseIgnoringCancellation / waitForTrackToFinish）の移植。実差し替え境界を増やさないため
/// interface ではなく拡張メソッドで実装する（CLAUDE.md §3-5）。
/// </summary>
public static class WaitForTrackToFinishExtensions
{
    /// <summary>
    /// 後始末用 pause。キャンセル済みフロー内では <see cref="System.Net.Http.HttpClient"/> がリクエストを
    /// 送らず取り消すため、キャンセルを継承しない Task で実行して確実に Spotify へ届ける（完全静寂, CLAUDE.md §3-1）。
    /// <paramref name="restoringVolume"/> を渡すと停止後にアプリ音量を戻す（ダッキング中の停止で
    /// Spotify が小音量のまま残らないように）。
    /// </summary>
    public static Task PauseIgnoringCancellationAsync(
        this ISpotifyController spotify, int? restoringVolume = null)
    {
        return Task.Run(async () =>
        {
            try
            {
                await spotify.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                if (restoringVolume is int volume)
                {
                    await spotify.SetVolumeAsync(volume, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // 後始末（ベストエフォート）なので伝播させない。
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 指定 URI の曲を**終わりまで見届けて**戻る（S10 fix、S12 fix で stale 耐性を強化）。
    /// 1) URI 切替確認: 再生切替直後は <c>/me/player</c> が前の曲のメタデータを返すことがあるため、
    ///    切替を確認してから曲長・位置を読む。曲長は **URI と同一スナップショット**の
    ///    <see cref="PlayerState.DurationSeconds"/> を使う（別リクエストで取り直すと直前の曲の曲長を掴み、
    ///    前の曲の長さで途中切りする。S12 で実際に踏んだ）。
    /// 2) 残り <paramref name="marginSeconds"/> 手前まで、<paramref name="recheckSeconds"/> 上限のチャンクで寝て、
    ///    起きるたびに位置を読み直す（snapshot が stale でも自己修正、タイマー遅延でもデッドエアが伸びない）。
    /// 3) 実終端の検知: 停止 / 別トラック遷移 / 位置が終端到達 / 位置停滞のいずれかで戻る。
    ///
    /// **終了らしき観測はすべて二重確認する（S12 fix-2）**: <c>/me/player</c> は再生中でも稀に stale な
    /// スナップショット（前の曲・paused 等）を返す。1 回の観測で「遷移した」「停止した」を確定すると
    /// その瞬間に呼び出し側の pause が走って曲の途中切りになるため、一拍おいた再観測でも同じ結論のときだけ確定する。
    /// 戻り値は復帰理由（診断ログ用）。
    /// </summary>
    public static async Task<TrackFinishReason> WaitForTrackToFinishAsync(
        this ISpotifyController spotify,
        string uri,
        IClock clock,
        double switchPollSeconds = 0.2,
        int switchMaxAttempts = 15,
        double marginSeconds = 5,
        double endPollSeconds = 0.5,
        double recheckSeconds = 30,
        CancellationToken ct = default)
    {
        // 終了らしき観測の二重確認。終了なら理由を、stale（再観測で再生中）なら null + 最新状態を返す。
        async Task<(TrackFinishReason? Reason, PlayerState Latest)> ConfirmEndAsync(PlayerState first)
        {
            if (first.State == PlaybackState.Playing && first.TrackUri == uri)
            {
                return (null, first);
            }
            await clock.DelayAsync(endPollSeconds, ct).ConfigureAwait(false);
            var second = await spotify.PlayerStateAsync(ct).ConfigureAwait(false);
            if (second.State == PlaybackState.Playing && second.TrackUri == uri)
            {
                return (null, second); // stale を踏んだだけ
            }
            if (second.TrackUri is string other && other != uri)
            {
                return (TrackFinishReason.TrackChanged, second);
            }
            return (TrackFinishReason.Stopped, second);
        }

        // 1) URI 切替確認 + 同一スナップショットの曲長・位置
        var duration = 0.0;
        var position = 0.0;
        for (var attempt = 0; attempt < switchMaxAttempts; attempt++)
        {
            var state = await spotify.PlayerStateAsync(ct).ConfigureAwait(false);
            if (state.TrackUri == uri)
            {
                duration = state.DurationSeconds;
                if (duration <= 0)
                {
                    // スナップショットに曲長を持たない実装だけ別問い合わせに倒す。
                    // 別問い合わせの一過性失敗は握り潰す（Mac の `try?` 相当。曲長 0 のまま次の試行で読み直す）。
                    // キャンセルだけは伝播させる（完全静寂の後始末は呼び出し側 finally が担う）。
                    try
                    {
                        duration = await spotify.CurrentTrackDurationSecondsAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        duration = 0;
                    }
                }
                if (duration > 0)
                {
                    position = state.PositionSeconds;
                    break;
                }
            }
            if (attempt < switchMaxAttempts - 1)
            {
                await clock.DelayAsync(switchPollSeconds, ct).ConfigureAwait(false);
            }
        }

        // 2) 残り margin 手前まで「チャンク寝 → 位置の読み直し」を繰り返す。
        //    budget はリピート再生・位置が進まない異常系での無限待ち防止の安全弁。
        var budget = Math.Max(duration - position, 0) + 60;
        while (duration > 0)
        {
            var remaining = duration - position;
            if (remaining <= marginSeconds)
            {
                break;
            }
            var nap = Math.Min(remaining - marginSeconds, recheckSeconds);
            if (budget < nap)
            {
                return TrackFinishReason.TimedOut;
            }
            await clock.DelayAsync(nap, ct).ConfigureAwait(false);
            budget -= nap;

            var observed = await spotify.PlayerStateAsync(ct).ConfigureAwait(false);
            var (reason, latest) = await ConfirmEndAsync(observed).ConfigureAwait(false);
            if (reason is TrackFinishReason confirmed)
            {
                return confirmed;
            }
            position = latest.PositionSeconds;
            if (latest.DurationSeconds > 0)
            {
                duration = latest.DurationSeconds;
            }
        }

        // 3) 実終端の検知（margin + 10 秒で打ち切り = 暴走防止の保険）
        var lastPosition = -1.0;
        var waited = 0.0;
        while (waited < marginSeconds + 10)
        {
            var observed = await spotify.PlayerStateAsync(ct).ConfigureAwait(false);
            var (reason, latest) = await ConfirmEndAsync(observed).ConfigureAwait(false);
            if (reason is TrackFinishReason confirmed)
            {
                return confirmed;
            }
            if (duration > 0 && latest.PositionSeconds >= duration - 1)
            {
                return TrackFinishReason.ReachedEnd;
            }
            if (latest.PositionSeconds == lastPosition)
            {
                return TrackFinishReason.PositionStalled;
            }
            lastPosition = latest.PositionSeconds;
            await clock.DelayAsync(endPollSeconds, ct).ConfigureAwait(false);
            waited += endPollSeconds;
        }
        return TrackFinishReason.TimedOut;
    }
}
