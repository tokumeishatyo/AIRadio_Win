namespace AIRadio.Core;

/// <summary>放送進行中の出来事（トレイ状態表示・ログ用）。値等価（テストは全ペイロード比較）。</summary>
public abstract record BroadcastEvent
{
    /// <summary>セグメント開始。</summary>
    public sealed record SegmentStarted(int Index, SegmentKind Kind) : BroadcastEvent;

    /// <summary>セグメント正常終了。</summary>
    public sealed record SegmentFinished(int Index, SegmentKind Kind) : BroadcastEvent;

    /// <summary>セグメントが実行時エラーで中断（fail-tolerant: スキップ / critical: この後 broadcast 中止）。</summary>
    public sealed record SegmentFailed(int Index, SegmentKind Kind, string Code, string Detail) : BroadcastEvent;

    /// <summary>番組を最後まで完走（正常終了時のみ。中止・キャンセルでは発火しない）。</summary>
    public sealed record BroadcastFinished : BroadcastEvent;
}

/// <summary>
/// 番組進行エンジン（W7）。<see cref="ProgramFormat"/> のセグメント列を宣言順に逐次実行し、各セグメントを
/// 種別で振り分ける（OP/news/ED = <see cref="ThemeSequencer"/>, talk = <see cref="CornerEngine"/>,
/// news 原稿 = 注入デリゲート）。Mac 版 `BroadcastEngine`（s7 当時）の移植。
///
/// 進行規則（CLAUDE.md §3-1, docs/specs/w7-broadcast-engine.md §4）:
/// - **fail-tolerant**: セグメント失敗はスキップして放送継続（<see cref="BroadcastEvent.SegmentFailed"/> 通知）。
///   ただし <see cref="ProgramSegment.Critical"/>（既定 OP）は失敗時に放送中止（<see cref="BroadcastException"/>）。
/// - **キャンセル即時伝播**: キャンセルはスキップと誤判定せず即時 throw。
/// - **完全静寂**: 正常終了・失敗・キャンセルのいずれでも最後に必ず <c>Pause</c>。
///
/// 実差し替え境界ではない（実装 1 個）ため interface 化しない（§3-5）。テストは協調オブジェクト
/// （<see cref="ThemeSequencer"/> / <see cref="CornerEngine"/>）へ fake を注入し、具象クラスのまま検証する。
/// W10 でローリング先読み準備（窓 2）、W13 でコーナー数 N 駆動生成・ED で終了へ進化する。
/// </summary>
public sealed class BroadcastEngine
{
    private readonly ThemeSequencer _themeSequencer;
    private readonly CornerEngine _cornerRunner;
    private readonly Func<CancellationToken, Task<string>> _newsAnnouncement;
    private readonly ISpotifyController _spotify;
    private readonly Action<BroadcastEvent>? _onEvent;

    /// <param name="newsAnnouncement">
    /// ニュース原稿を返すデリゲート。Core は Infrastructure（<c>NewsWeatherProvider</c>）を参照できないため
    /// （§3-4 単方向依存）、App が <c>ct =&gt; provider.AnnouncementAsync(ct)</c> を渡す。news 出現のたびに呼ぶ。
    /// </param>
    public BroadcastEngine(
        ThemeSequencer themeSequencer,
        CornerEngine cornerRunner,
        Func<CancellationToken, Task<string>> newsAnnouncement,
        ISpotifyController spotify,
        Action<BroadcastEvent>? onEvent = null)
    {
        _themeSequencer = themeSequencer;
        _cornerRunner = cornerRunner;
        _newsAnnouncement = newsAnnouncement;
        _spotify = spotify;
        _onEvent = onEvent;
    }

    /// <summary>番組を 1 本通しで実行する（単一のキャンセル可能 Task として呼ばれる前提）。</summary>
    public async Task RunAsync(
        ProgramFormat format,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        CancellationToken ct = default)
    {
        // 1. preflight（fail-fast、音を出す前）: anchor DJ と talk の corner_id を解決。
        var anchor = djs.FirstOrDefault(d => d.Id == format.AnchorDjId)
            ?? throw ConfigException.MissingField($"program.anchor_dj_id に未定義の DJ: {format.AnchorDjId}");
        foreach (var seg in format.Segments)
        {
            if (seg.Kind != SegmentKind.Talk)
            {
                continue;
            }
            var id = seg.CornerId
                ?? throw ConfigException.MissingField("program.segments[].corner_id（talk は必須）");
            if (corners.All(c => c.Id != id))
            {
                throw ConfigException.MissingField($"program.segments[].corner_id に未定義のコーナー: {id}");
            }
        }

        // 2. セグメントを宣言順に逐次実行。
        try
        {
            for (var i = 0; i < format.Segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await RunSegmentAsync(i, format.Segments[i], themes, corners, djs, anchor, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // 完全静寂（§3-1）: 中止（critical）・キャンセルでも BGM を止め切る（キャンセル非継承）。
            await _spotify.PauseIgnoringCancellationAsync().ConfigureAwait(false);
            throw;
        }

        // 正常完走: 各セグメントも自前で pause するが、エンジンでも重ねて保証してから完了を通知。
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
        _onEvent?.Invoke(new BroadcastEvent.BroadcastFinished());
    }

    private async Task RunSegmentAsync(
        int index,
        ProgramSegment segment,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        CancellationToken ct)
    {
        _onEvent?.Invoke(new BroadcastEvent.SegmentStarted(index, segment.Kind));
        try
        {
            await PerformAsync(segment, themes, corners, djs, anchor, ct).ConfigureAwait(false);
            _onEvent?.Invoke(new BroadcastEvent.SegmentFinished(index, segment.Kind));
        }
        catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
        {
            // キャンセルは即時伝播。スキップ（SegmentFailed）と誤判定しない（§4-4）。
            // Infra がキャンセルをドメインエラーにラップして投げても、ct を見て OperationCanceledException に倒す。
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            // fail-tolerant: 失敗セグメントを静音化し、記録して次へ。critical は放送中止。
            await _spotify.PauseIgnoringCancellationAsync().ConfigureAwait(false);
            var code = (ex as IRadioError)?.Code ?? ex.GetType().Name;
            _onEvent?.Invoke(new BroadcastEvent.SegmentFailed(index, segment.Kind, code, ex.Message));
            if (segment.Critical)
            {
                throw BroadcastException.SegmentFailed($"{segment.Kind}: {code}");
            }
        }
    }

    private async Task PerformAsync(
        ProgramSegment segment,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        CancellationToken ct)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Opening:
                await _themeSequencer
                    .RunAsync(themes.Opening, themes.OpeningAnnouncement, anchor.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;

            case SegmentKind.Talk:
                // preflight 済みなので必ず見つかる。
                var corner = corners.First(c => c.Id == segment.CornerId);
                await _cornerRunner.RunAsync(corner, djs, ct).ConfigureAwait(false);
                break;

            case SegmentKind.News:
                // ニュース原稿は出現のたびに取得（注入デリゲート経由）。NewsWeatherProvider は取得失敗を
                // 握り潰しキャンセルのみ throw するため、エンジン側のニュース固有エラー処理は不要。
                var script = await _newsAnnouncement(ct).ConfigureAwait(false);
                await _themeSequencer
                    .RunAsync(themes.News, script, anchor.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;

            case SegmentKind.Ending:
                await _themeSequencer
                    .RunAsync(themes.Ending, themes.EndingAnnouncement, anchor.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;
        }
    }
}
