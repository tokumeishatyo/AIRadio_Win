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

    /// <summary>冒頭曲の再生開始（フォールバック曲のときは Track の Title/Artist 空）。</summary>
    public sealed record SongStarted(int Index, TrackInfo Track) : BroadcastEvent;

    /// <summary>冒頭曲（フル再生）の終端検知の理由（途中切り診断用）。</summary>
    public sealed record SongFinished(int Index, TrackFinishReason Reason) : BroadcastEvent;

    /// <summary>番組を最後まで完走（正常終了時のみ。中止・キャンセルでは発火しない）。</summary>
    public sealed record BroadcastFinished : BroadcastEvent;
}

/// <summary>
/// 番組進行エンジン（W7）。<see cref="ProgramFormat"/> のセグメント列を宣言順に逐次実行し、各セグメントを
/// 種別で振り分ける（OP/news/ED = <see cref="ThemeSequencer"/>, talk = <see cref="CornerEngine"/>,
/// 冒頭曲 = <see cref="SongPicker"/>, news 原稿 = 注入デリゲート）。Mac 版 `BroadcastEngine` の移植。
///
/// <para>**ローリング先読み準備（window=2）**: 実行中セグメントの先 2 つまでの準備（選曲 / LLM 台本 + 全行 TTS /
/// ニュース原稿）を裏で起動して保持する。これにより、OP と冒頭曲の再生中に次のトーク台本が準備され、
/// セグメント間の無音（デッドエア）をゼロにする（CLAUDE.md §3-1 の「放送中の完全無音」）。</para>
///
/// 進行規則（docs/specs/w7-broadcast-engine.md §4）:
/// - **fail-tolerant**: セグメント失敗はスキップして放送継続（<see cref="BroadcastEvent.SegmentFailed"/>）。
///   ただし <see cref="ProgramSegment.Critical"/>（既定 OP）は失敗時に放送中止（<see cref="BroadcastException"/>）。
/// - **キャンセル即時伝播**: キャンセルはスキップと誤判定せず即時 throw。保持中の準備 Task も連動して止める。
/// - **完全静寂**: 正常終了・失敗・キャンセルのいずれでも最後に必ず <c>Pause</c>。
///
/// 実差し替え境界ではない（実装 1 個）ため interface 化しない（§3-5）。テストは協調オブジェクトへ fake を注入する。
/// </summary>
public sealed class BroadcastEngine
{
    /// <summary>実行中セグメントの先いくつまで準備するか。</summary>
    public const int PreparationWindow = 2;

    private readonly ThemeSequencer _themeSequencer;
    private readonly CornerEngine _cornerRunner;
    private readonly SongPicker? _songPicker;
    private readonly Func<CancellationToken, Task<string>> _newsAnnouncement;
    private readonly ISpotifyController _spotify;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly Action<BroadcastEvent>? _onEvent;

    /// <param name="songPicker">冒頭曲の選曲（null・失敗時は <see cref="SongSegmentSpec.FallbackTrackUri"/> に倒す）。</param>
    /// <param name="newsAnnouncement">
    /// ニュース原稿を返すデリゲート。Core は Infrastructure（<c>NewsWeatherProvider</c>）を参照できないため
    /// （§3-4）、App が <c>ct =&gt; provider.AnnouncementAsync(ct)</c> を渡す。news 出現のたびに呼ぶ。
    /// </param>
    /// <param name="timeZone">時刻プレースホルダの解釈に使うタイムゾーン（W8。省略時は <see cref="TimeZoneInfo.Local"/>）。</param>
    public BroadcastEngine(
        ThemeSequencer themeSequencer,
        CornerEngine cornerRunner,
        SongPicker? songPicker,
        Func<CancellationToken, Task<string>> newsAnnouncement,
        ISpotifyController spotify,
        IClock clock,
        Action<BroadcastEvent>? onEvent = null,
        TimeZoneInfo? timeZone = null)
    {
        _themeSequencer = themeSequencer;
        _cornerRunner = cornerRunner;
        _songPicker = songPicker;
        _newsAnnouncement = newsAnnouncement;
        _spotify = spotify;
        _clock = clock;
        _onEvent = onEvent;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
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
            if (seg.Kind == SegmentKind.Talk)
            {
                var id = seg.CornerId
                    ?? throw ConfigException.MissingField("program.segments[].corner_id（talk は必須）");
                if (corners.All(c => c.Id != id))
                {
                    throw ConfigException.MissingField($"program.segments[].corner_id に未定義のコーナー: {id}");
                }
            }
            else if (seg.Kind == SegmentKind.News && seg.DjId is string newsDjId && djs.All(d => d.Id != newsDjId))
            {
                throw ConfigException.MissingField($"program.segments[].dj_id（news）に未定義の DJ: {newsDjId}");
            }
        }

        // 準備 Task はエンジン本体のキャンセルに連動して止める（消費されない先読み分の後始末）。
        using var prepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ledger = new PreparationLedger();
        try
        {
            await BroadcastAsync(format, themes, corners, djs, anchor, ledger, prepCts.Token, ct).ConfigureAwait(false);
        }
        finally
        {
            // 保持中（未消費）の準備 Task をキャンセルし、観測して未観測例外を残さない。
            prepCts.Cancel();
            await ledger.DrainAsync().ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(
        ProgramFormat format,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        PreparationLedger ledger,
        CancellationToken prepCt,
        CancellationToken ct)
    {
        var segments = format.Segments;
        var songContext = $"ラジオ番組「{format.Title}」のオープニング直後にかける、本日の1曲目";

        // ローリング準備の起動（単一消費者なので進捗はローカル変数で追う）。
        var preparationStartedThrough = -1;
        void EnsurePrepared(int target)
        {
            while (preparationStartedThrough < target)
            {
                preparationStartedThrough++;
                var index = preparationStartedThrough;
                if (index >= segments.Count)
                {
                    return;
                }
                StartPreparation(index, segments[index], corners, djs, songContext, ledger, prepCt);
            }
        }

        // 冒頭曲の選曲を待って {first_song}（OP の曲振り）を作る（放送開始前の無音は許容ゾーン）。
        EnsurePrepared(PreparationWindow);
        var extraValues = new Dictionary<string, string> { ["first_song"] = "本日の一曲" };
        if (segments.Count > 1 && segments[1].Kind == SegmentKind.Song && ledger.SongTask(1) is Task<TrackInfo> pick)
        {
            var track = await pick.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(track.Title))
            {
                extraValues["first_song"] = $"{track.Artist}で、「{track.Title}」";
            }
        }
        ct.ThrowIfCancellationRequested();

        try
        {
            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                EnsurePrepared(i + PreparationWindow);
                await RunSegmentAsync(i, segments[i], themes, djs, anchor, extraValues, ledger, ct).ConfigureAwait(false);
                ledger.Discard(i);
            }
        }
        catch
        {
            // 完全静寂（§3-1）: 中止（critical）・キャンセルでも BGM を止め切る（キャンセル非継承）。
            await _spotify.PauseIgnoringCancellationAsync().ConfigureAwait(false);
            throw;
        }

        // 正常完走: 各セグメントも自前で pause するが、エンジンでも重ねて保証してから完了を通知。
        try { await _spotify.PauseAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        _onEvent?.Invoke(new BroadcastEvent.BroadcastFinished());
    }

    private void StartPreparation(
        int index,
        ProgramSegment segment,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        string songContext,
        PreparationLedger ledger,
        CancellationToken prepCt)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Talk:
                // corner は preflight 済み（必ず見つかる）。準備（LLM 台本 + 全行 TTS）を裏で起動。
                var corner = corners.First(c => c.Id == segment.CornerId);
                ledger.AddCorner(index, _cornerRunner.PrepareAsync(corner, djs, prepCt));
                break;

            case SegmentKind.Song:
                var spec = segment.Song!;
                ledger.AddSong(index, PickSongAsync(spec, songContext, prepCt));
                break;

            case SegmentKind.News:
                // 出現のたびに生成（長時間放送でニュースが更新される）。
                ledger.AddNews(index, _newsAnnouncement(prepCt));
                break;

            case SegmentKind.Opening:
            case SegmentKind.Ending:
                break; // 準備なし
        }
    }

    private async Task<TrackInfo> PickSongAsync(SongSegmentSpec spec, string context, CancellationToken ct)
    {
        if (_songPicker is not null)
        {
            try
            {
                return await _songPicker
                    .PickAsync(new SongRequest(context, spec.FallbackTrackUri, spec.PromptHint), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 選曲失敗（LLM 不調等）はフォールバック曲に倒す（放送は止めない）。
            }
        }
        return new TrackInfo(spec.FallbackTrackUri, "", "");
    }

    private async Task RunSegmentAsync(
        int index,
        ProgramSegment segment,
        BroadcastThemes themes,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        IReadOnlyDictionary<string, string> extraValues,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        _onEvent?.Invoke(new BroadcastEvent.SegmentStarted(index, segment.Kind));
        try
        {
            await PerformAsync(index, segment, themes, djs, anchor, extraValues, ledger, ct).ConfigureAwait(false);
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
        int index,
        ProgramSegment segment,
        BroadcastThemes themes,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        IReadOnlyDictionary<string, string> extraValues,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Opening:
                await _themeSequencer
                    .RunAsync(themes.Opening, Expand(themes.Greetings, themes.OpeningAnnouncement, extraValues), anchor.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;

            case SegmentKind.Song:
                // 選曲は先行準備済み（OP 前に確定している）。
                var spec = segment.Song!;
                var songTask = ledger.SongTask(index)
                    ?? throw BroadcastException.SegmentFailed($"song: 準備タスクがありません（index {index}）");
                var track = await songTask.ConfigureAwait(false);
                await _spotify.PlayAsync(track.Uri, ct).ConfigureAwait(false);
                await _spotify.SetVolumeAsync(spec.Volume, ct).ConfigureAwait(false);
                _onEvent?.Invoke(new BroadcastEvent.SongStarted(index, track));
                if (spec.PlaySeconds > 0)
                {
                    await _clock.DelayAsync(spec.PlaySeconds, ct).ConfigureAwait(false);
                }
                else
                {
                    var reason = await _spotify.WaitForTrackToFinishAsync(track.Uri, _clock, ct: ct).ConfigureAwait(false);
                    _onEvent?.Invoke(new BroadcastEvent.SongFinished(index, reason));
                }
                await _spotify.PauseAsync(ct).ConfigureAwait(false);
                break;

            case SegmentKind.Talk:
                // 先行準備の完了を待つ（通常は OP・冒頭曲の再生中に完了しており待ち時間ゼロ）。
                var cornerTask = ledger.CornerTask(index)
                    ?? throw BroadcastException.SegmentFailed($"talk: 準備タスクがありません（index {index}）");
                var prepared = await cornerTask.ConfigureAwait(false);
                await _cornerRunner.RunAsync(prepared, djs, ct).ConfigureAwait(false);
                break;

            case SegmentKind.News:
                var newsTask = ledger.NewsTask(index)
                    ?? throw BroadcastException.SegmentFailed($"news: 準備タスクがありません（index {index}）");
                var script = await newsTask.ConfigureAwait(false);
                // 専任のニュース読み手（program の dj_id。未指定は anchor）。preflight 検証済み。
                var newsSpeaker = segment.DjId is string newsDjId ? djs.First(d => d.Id == newsDjId) : anchor;
                await _themeSequencer
                    .RunAsync(themes.News, Expand(themes.Greetings, script, extraValues), newsSpeaker.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;

            case SegmentKind.Ending:
                await _themeSequencer
                    .RunAsync(themes.Ending, Expand(themes.Greetings, themes.EndingAnnouncement, extraValues), anchor.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// 時刻プレースホルダ（<c>{greeting}</c>/<c>{month}</c>/<c>{day}</c>/<c>{ampm}</c>/<c>{hour}</c>/<c>{hour12}</c>/<c>{minute}</c>）と
    /// 番組コンテキスト（<c>{first_song}</c> 等）を展開する（未知キーは原文保持）。**各セグメントの発話直前に**
    /// <see cref="IClock.Now"/> を読むため、ローリング先読み準備で数分先行しても時報が再生時点で正確（w8 §3）。
    /// ニュースは Provider が <c>{news}</c>/<c>{weather}</c> を準備時に展開済みの原稿を渡し、ここで時刻を被せる二段展開。
    /// </summary>
    private string Expand(Greetings greetings, string template, IReadOnlyDictionary<string, string> extra)
    {
        var values = new Dictionary<string, string>(TimePhrases.Values(_clock.Now, greetings, _timeZone));
        foreach (var (key, value) in extra)
        {
            values[key] = value; // {first_song} 等が衝突時に優先（Mac の merge { _, new in new } と同義）。
        }
        return TemplateExpander.Expand(template, values);
    }

    /// <summary>
    /// ローリング準備の台帳。準備 Task（選曲 / 台本 / ニュース原稿）の保持・消費・破棄をスレッドセーフに行う
    /// （準備は並行に完了し、後始末は finally から呼ばれる）。
    /// </summary>
    private sealed class PreparationLedger
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, Task<TrackInfo>> _songTasks = new();
        private readonly Dictionary<int, Task<PreparedCorner>> _cornerTasks = new();
        private readonly Dictionary<int, Task<string>> _newsTasks = new();

        public void AddSong(int index, Task<TrackInfo> task) { lock (_lock) { _songTasks[index] = task; } }
        public void AddCorner(int index, Task<PreparedCorner> task) { lock (_lock) { _cornerTasks[index] = task; } }
        public void AddNews(int index, Task<string> task) { lock (_lock) { _newsTasks[index] = task; } }

        public Task<TrackInfo>? SongTask(int index) { lock (_lock) { return _songTasks.GetValueOrDefault(index); } }
        public Task<PreparedCorner>? CornerTask(int index) { lock (_lock) { return _cornerTasks.GetValueOrDefault(index); } }
        public Task<string>? NewsTask(int index) { lock (_lock) { return _newsTasks.GetValueOrDefault(index); } }

        /// <summary>消費済みの準備を破棄する（ローリング窓の後端）。</summary>
        public void Discard(int index)
        {
            lock (_lock)
            {
                _songTasks.Remove(index);
                _cornerTasks.Remove(index);
                _newsTasks.Remove(index);
            }
        }

        /// <summary>保持中（未消費）の準備 Task をすべて観測する（キャンセル後の未観測例外を防ぐ）。</summary>
        public async Task DrainAsync()
        {
            List<Task> tasks;
            lock (_lock)
            {
                tasks = new List<Task>(_songTasks.Count + _cornerTasks.Count + _newsTasks.Count);
                tasks.AddRange(_songTasks.Values);
                tasks.AddRange(_cornerTasks.Values);
                tasks.AddRange(_newsTasks.Values);
            }
            foreach (var task in tasks)
            {
                try { await task.ConfigureAwait(false); }
                catch { /* 破棄された準備（キャンセル/失敗）。ここでは観測のみ。 */ }
            }
        }
    }
}
