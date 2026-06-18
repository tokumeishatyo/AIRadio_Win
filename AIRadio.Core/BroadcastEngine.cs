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

    /// <summary>「ED で終了」要求を受け付けた（残りのコーナーを飛ばして ED へ。仕様 s13/w13 §3-4）。</summary>
    public sealed record EndingRequested : BroadcastEvent;

    /// <summary>番組を最後まで完走（正常終了時のみ。中止・キャンセルでは発火しない）。</summary>
    public sealed record BroadcastFinished : BroadcastEvent;
}

/// <summary>
/// 番組進行エンジン（W7 → W13）。<see cref="ProgramPlan"/> がコーナー数 N から決定論的に生成するセグメントを
/// index 順に実行し（W13。v1 の <c>ProgramFormat</c> セグメント列駆動を置換）、各セグメントを種別で振り分ける
/// （OP/news/ED = <see cref="ThemeSequencer"/>, talk = <see cref="CornerEngine"/>,
/// 冒頭曲 = <see cref="SongPicker"/>, news 原稿 = 注入デリゲート）。Mac 版 `BroadcastEngine` の移植。
///
/// <para>**ローリング先読み準備（window=2）**: 実行中セグメントの先 2 つまでの準備（選曲 / LLM 台本 + 全行 TTS /
/// ニュース原稿）を裏で起動して保持する。これにより、OP と冒頭曲の再生中に次のトーク台本が準備され、
/// セグメント間の無音（デッドエア）をゼロにする（CLAUDE.md §3-1 の「放送中の完全無音」）。準備 Task は
/// セグメント index ごとの linked CTS（<see cref="PreparationLedger"/>）で管理し、停止・ED で選択的にキャンセルする。</para>
///
/// <para>**「ED で終了」（W13）**: <see cref="BroadcastControl.RequestEnding"/> を受けると、各セグメント完了時に
/// 現セグメント（+ 準備完了済みの直後 free_talk）を流したあと残りを飛ばして ED で締める（§3-4）。</para>
///
/// 進行規則（docs/specs/w7-broadcast-engine.md §4 / w13-program-generation.md §3）:
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
    /// <summary>ゲスト選定用の乱数（count → 0..count-1。テストは決定論的に注入。W14）。</summary>
    private readonly Func<int, int> _randomIndex;

    /// <summary>アーティスト特集ランナー（W15。null＝特集無効。特集有効なのに null なら preflight で fail-fast）。</summary>
    private readonly ArtistFeatureEngine? _artistFeatureRunner;

    /// <summary>長期記憶の永続化（W18。null＝ジャーナル無効。<see cref="_journalSummarizer"/> と両方揃ったときだけ保存・注入する）。</summary>
    private readonly IJournalStore? _journalStore;

    /// <summary>ハイライト要約器（W18。null＝ジャーナル無効）。</summary>
    private readonly JournalSummarizer? _journalSummarizer;

    /// <param name="songPicker">冒頭曲の選曲（null・失敗時は <see cref="SongSegmentSpec.FallbackTrackUri"/> に倒す）。</param>
    /// <param name="newsAnnouncement">
    /// ニュース原稿を返すデリゲート。Core は Infrastructure（<c>NewsWeatherProvider</c>）を参照できないため
    /// （§3-4）、App が <c>ct =&gt; provider.AnnouncementAsync(ct)</c> を渡す。news 出現のたびに呼ぶ。
    /// </param>
    /// <param name="timeZone">時刻プレースホルダの解釈に使うタイムゾーン（W8。省略時は <see cref="TimeZoneInfo.Local"/>）。</param>
    /// <param name="randomIndex">ゲスト・アーティスト選定用の乱数（W14/W15。省略時は <see cref="Random.Shared"/>）。</param>
    /// <param name="artistFeatureRunner">アーティスト特集ランナー（W15。末尾 optional＝既存 positional 呼出を壊さない。特集無効なら不要）。</param>
    /// <param name="journalStore">長期記憶の永続化（W18。末尾 optional。null なら保存・注入なし＝従来挙動）。</param>
    /// <param name="journalSummarizer">ハイライト要約器（W18。末尾 optional。<paramref name="journalStore"/> と両方揃ったときだけ保存する）。</param>
    public BroadcastEngine(
        ThemeSequencer themeSequencer,
        CornerEngine cornerRunner,
        SongPicker? songPicker,
        Func<CancellationToken, Task<string>> newsAnnouncement,
        ISpotifyController spotify,
        IClock clock,
        Action<BroadcastEvent>? onEvent = null,
        TimeZoneInfo? timeZone = null,
        Func<int, int>? randomIndex = null,
        ArtistFeatureEngine? artistFeatureRunner = null,
        IJournalStore? journalStore = null,
        JournalSummarizer? journalSummarizer = null)
    {
        _themeSequencer = themeSequencer;
        _cornerRunner = cornerRunner;
        _songPicker = songPicker;
        _newsAnnouncement = newsAnnouncement;
        _spotify = spotify;
        _clock = clock;
        _onEvent = onEvent;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _randomIndex = randomIndex ?? (n => Random.Shared.Next(n));
        _artistFeatureRunner = artistFeatureRunner;
        _journalStore = journalStore;
        _journalSummarizer = journalSummarizer;
    }

    /// <summary>番組を 1 本通しで実行する（単一のキャンセル可能 Task として呼ばれる前提）。</summary>
    /// <param name="plan">コーナー数 N からセグメントを生成する番組（W13）。</param>
    /// <param name="control">「ED で終了」操作ハンドル（null なら ED 終了なし）。</param>
    /// <param name="guests">ゲストコーナー（W14）のプール（省略時 空＝ゲスト無効）。放送開始時に 1 名選定する。</param>
    /// <param name="artists">アーティスト特集（W15）のプール（省略時 空）。空でも fail-fast せず実行時にスキップ（§18-3）。放送開始時に 1 組選定。</param>
    public async Task RunAsync(
        ProgramPlan plan,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        BroadcastControl? control = null,
        IReadOnlyList<DjProfile>? guests = null,
        IReadOnlyList<ArtistProfile>? artists = null,
        CancellationToken ct = default)
    {
        // 1. preflight（fail-fast、音を出す前）: anchor DJ・talk/letter コーナー・news 読み手を blueprint から検証。
        var anchor = djs.FirstOrDefault(d => d.Id == plan.AnchorDjId)
            ?? throw ConfigException.MissingField($"program.anchor_dj_id に未定義の DJ: {plan.AnchorDjId}");
        foreach (var cornerId in new[] { plan.Blueprint.TalkCornerId, plan.Blueprint.LetterCornerId })
        {
            if (corners.All(c => c.Id != cornerId))
            {
                throw ConfigException.MissingField($"program.talk/letter.corner_id に未定義のコーナー: {cornerId}");
            }
        }
        if (plan.Blueprint.NewsDjId is string newsDjId && djs.All(d => d.Id != newsDjId))
        {
            throw ConfigException.MissingField($"program.news.dj_id に未定義の DJ: {newsDjId}");
        }

        // ゲストコーナー検証＋選定（W14）。実際にゲスト枠が出る放送のときだけ行う
        //（N<2 等でゲストが出ないなら、プール空でも誤って放送中止しない）。
        DjProfile? selectedGuest = null;
        var guestPool = guests ?? Array.Empty<DjProfile>();
        if (plan.Blueprint.GuestCornerId is string guestCornerId && plan.IncludesGuestCorner)
        {
            if (corners.All(c => c.Id != guestCornerId))
            {
                throw ConfigException.MissingField($"program.guest.corner_id に未定義のコーナー: {guestCornerId}");
            }
            // ゲストコーナー id はトーク／お便りと別物でなければならない（同じだと全トークがゲスト化する）。
            if (guestCornerId == plan.Blueprint.TalkCornerId || guestCornerId == plan.Blueprint.LetterCornerId)
            {
                throw ConfigException.MissingField($"program.guest.corner_id が talk/letter と重複: {guestCornerId}");
            }
            if (guestPool.Count == 0)
            {
                throw ConfigException.MissingField("guests: ゲストプールが空です（ゲストコーナー有効時は必須）");
            }
            var collision = guestPool.FirstOrDefault(g => djs.Any(d => d.Id == g.Id));
            if (collision is not null)
            {
                throw ConfigException.MissingField($"guests にレギュラーと衝突する id: {collision.Id}");
            }
            selectedGuest = guestPool[_randomIndex(guestPool.Count)];
        }

        // アーティスト特集の検証＋選定（W15）。実際に特集が出る放送のときだけ（特集はゲストに従属＝IncludesArtistFeature）。
        // ゲストと異なり、プール空でも fail-fast しない（実行時に E-ART-EMPTY-POOL-001 でスキップ＝§18-3）。
        ArtistProfile? selectedArtist = null;
        var artistPool = artists ?? Array.Empty<ArtistProfile>();
        if (plan.Blueprint.ArtistFeatureCornerId is string featureCornerId && plan.IncludesArtistFeature)
        {
            if (corners.All(c => c.Id != featureCornerId))
            {
                throw ConfigException.MissingField($"program.artist_feature.corner_id に未定義のコーナー: {featureCornerId}");
            }
            // 特集コーナー id はトーク／お便り／ゲストと別物でなければならない。
            if (featureCornerId == plan.Blueprint.TalkCornerId || featureCornerId == plan.Blueprint.LetterCornerId
                || featureCornerId == plan.Blueprint.GuestCornerId)
            {
                throw ConfigException.MissingField($"program.artist_feature.corner_id が talk/letter/guest と重複: {featureCornerId}");
            }
            if (_artistFeatureRunner is null)
            {
                throw ConfigException.MissingField("artist_feature 有効だが ArtistFeatureEngine が未配線です");
            }
            // プール空（未生成）は throw せず選定をスキップ → 実行時に特集スキップ（§8-4）。
            if (artistPool.Count > 0)
            {
                selectedArtist = artistPool[_randomIndex(artistPool.Count)];
            }
        }

        // 当日の編成（先頭＝メイン）を解決（W13.5。空・未定義 id は fail-fast）。OP/ED/トークを仕切る。
        var castDjIds = plan.Blueprint.WeeklyCast.DjIds(_clock.Now, _timeZone);
        if (castDjIds.Count == 0)
        {
            throw ConfigException.MissingField("weekly_cast: 本日（曜日）の編成が定義されていません");
        }
        var cast = castDjIds.Select(id => djs.FirstOrDefault(d => d.Id == id)
            ?? throw ConfigException.MissingField($"weekly_cast に未定義の DJ: {id}")).ToList();
        var main = cast[0];

        // 準備 Task は ledger が index ごとの linked CTS（エンジン ct 連動）で管理する。
        var ledger = new PreparationLedger();
        try
        {
            await BroadcastAsync(plan, themes, corners, djs, anchor, cast, main, selectedGuest, selectedArtist, control, ledger, ct).ConfigureAwait(false);
        }
        finally
        {
            // 保持中（未消費）の準備 Task をキャンセルし、観測して未観測例外を残さない（生存エントリのみ対象）。
            ledger.CancelAll();
            await ledger.DrainAsync().ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(
        ProgramPlan plan,
        BroadcastThemes themes,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        DjProfile anchor,
        IReadOnlyList<DjProfile> cast,
        DjProfile main,
        DjProfile? guest,
        ArtistProfile? artist,
        BroadcastControl? control,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        var songContext = $"ラジオ番組「{plan.Title}」のオープニング直後にかける、本日の1曲目";
        var castDjIds = cast.Select(d => d.Id).ToList();
        // 冒頭挨拶を付ける「番組最初のトーク」セグメント index（通常 2。無ければ -1）。
        var firstTalkIndex = FirstTalkIndex(plan);
        // 冒頭コーナーの時刻連動の挨拶語（準備時点で解決。再生まで数分なので時間帯ズレはほぼ無い、W13.5 §3）。
        var greeting = TimePhrases.Values(_clock.Now, themes.Greetings, _timeZone).GetValueOrDefault("greeting");
        // 前回までの振り返り（長期記憶。当週のハイライトを冒頭コーナーの準備にのみ渡す。読み込み失敗は無視＝fail-tolerant。W18 §5/§6）。
        var journalContext = JournalContextFromCurrentWeek();

        // ローリング準備の起動（単一消費者なので進捗はローカル変数で追う）。
        var preparationStartedThrough = -1;
        void EnsurePrepared(int target)
        {
            while (preparationStartedThrough < target)
            {
                preparationStartedThrough++;
                var index = preparationStartedThrough;
                if (plan.Segment(index) is not ProgramSegment seg)
                {
                    return; // 有限番組の末尾（ED の次）に到達。
                }
                StartPreparation(index, seg, corners, djs, songContext, castDjIds, firstTalkIndex, greeting, journalContext, plan.Blueprint.GuestCornerId, guest, artist, ledger, ct);
            }
        }

        // 冒頭曲の選曲を待って {first_song}（OP の曲振り）を作る（放送開始前の無音は許容ゾーン）。
        EnsurePrepared(PreparationWindow);
        var extraValues = new Dictionary<string, string> { ["first_song"] = "本日の一曲" };
        if (plan.Segment(1) is { Kind: SegmentKind.Song } && ledger.SongTask(1) is Task<TrackInfo> pick)
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
            var index = 0;
            while (plan.Segment(index) is ProgramSegment segment)
            {
                ct.ThrowIfCancellationRequested();
                EnsurePrepared(index + PreparationWindow);
                await RunSegmentAsync(index, segment, themes, djs, anchor, main, extraValues, ledger, ct).ConfigureAwait(false);
                ledger.Discard(index);

                // 「ED で終了」: 直後のトーク（free_talk）が準備完了済みならそれだけ流し、
                // 残り（お便り / ニュース含む）はすべて飛ばして ED（仕様 s13/w13 §3-4）。
                if (control is { IsEndingRequested: true } && segment.Kind != SegmentKind.Ending)
                {
                    _onEvent?.Invoke(new BroadcastEvent.EndingRequested());
                    var edIndex = index + 1;
                    // アーティスト特集の直後は「直後トークを流す」分岐に入らず ED 直行（特集後に余分なトークを挟まない。§8-5）。
                    if (segment.Kind != SegmentKind.ArtistFeature
                        && plan.Segment(edIndex) is { Kind: SegmentKind.Talk, CornerId: var cornerId } nextTalk
                        && cornerId == plan.Blueprint.TalkCornerId
                        && ledger.IsCornerPrepared(edIndex))
                    {
                        ledger.CancelAll(keeping: edIndex);   // 窓の外を捨てる
                        await RunSegmentAsync(edIndex, nextTalk, themes, djs, anchor, main, extraValues, ledger, ct).ConfigureAwait(false);
                        ledger.Discard(edIndex);
                        edIndex++;
                    }
                    else
                    {
                        ledger.CancelAll();
                    }
                    // ED を流して終了（エンドレスは plan に ED が無いため合成。有限も前倒しで同じ）。
                    await RunSegmentAsync(edIndex, new ProgramSegment(SegmentKind.Ending), themes, djs, anchor, main, extraValues, ledger, ct).ConfigureAwait(false);
                    break;
                }
                index++;
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
        // 長期記憶（W18 §5）: 正常終了した回のみハイライトを記録（停止/キャンセルは catch で抜けてここに来ない＝記録しない）。
        await SaveJournalAsync(guest, artist, ct).ConfigureAwait(false);
        _onEvent?.Invoke(new BroadcastEvent.BroadcastFinished());
    }

    /// <summary>
    /// 開始時にジャーナルを読み、当週のハイライトを冒頭コーナー用の振り返り文へ（W18 §5/§6）。
    /// store 未注入・読み込み失敗・当週エントリ無しはいずれも空文字（注入なし。完全 fail-tolerant）。
    /// </summary>
    private string JournalContextFromCurrentWeek()
    {
        if (_journalStore is null)
        {
            return "";
        }
        StationJournal journal;
        try
        {
            journal = _journalStore.Load();
        }
        catch
        {
            return ""; // 壊れファイル・IO 失敗は握り潰して空に倒す（長期記憶は事故ゼロ系）。
        }
        return JournalContext(journal.EntriesForCurrentWeek(_clock.Now, _timeZone));
    }

    /// <summary>当週のハイライト群を冒頭コーナー用の振り返り文へ（直近 3 件まで・<c>・</c> 連結。空なら ""）。Mac <c>journalContext(from:)</c> 移植。</summary>
    private static string JournalContext(IReadOnlyList<JournalEntry> entries)
    {
        var recent = entries.Count <= 3 ? entries : entries.Skip(entries.Count - 3).ToList();
        return recent.Count == 0 ? "" : string.Join("\n", recent.Select(e => $"・{e.Highlight}"));
    }

    /// <summary>
    /// 番組終了時（正常終了のみ）に、その回のハイライトを要約してジャーナルへ追記（W18 §2/§5）。
    /// 完全 fail-tolerant: 要約・保存の失敗は放送に影響させない。ゲストも特集も無い回は記録しない（確定 A）。
    /// </summary>
    private async Task SaveJournalAsync(DjProfile? guest, ArtistProfile? artist, CancellationToken ct)
    {
        if (_journalStore is null || _journalSummarizer is null)
        {
            return; // ジャーナル無効。
        }
        var digest = new BroadcastDigest(DateString(_clock.Now, _timeZone), guest?.Name, artist?.Name);
        if (!digest.HasContent)
        {
            return; // ゲストも特集も無い回は記録しない（確定 A）。
        }
        var highlight = await _journalSummarizer.SummarizeAsync(digest, ct).ConfigureAwait(false);
        if (highlight.Length == 0)
        {
            return; // 要約が空（素材なしフォールバック）なら記録しない。
        }
        StationJournal current;
        try { current = _journalStore.Load(); }
        catch { current = StationJournal.Empty; } // 壊れていても新規に積む（前週分の喪失は許容＝事故ゼロ系）。
        var updated = current.Appended(new JournalEntry(digest.Date, highlight), _clock.Now, _timeZone);
        try { _journalStore.Save(updated); }
        catch { /* 保存失敗は放送に影響させない（fail-tolerant）。 */ }
    }

    /// <summary>放送日の日付文字列 "YYYY-MM-DD"（指定タイムゾーンで解釈）。Mac <c>dateString</c> 移植。</summary>
    private static string DateString(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        return $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}";
    }

    private void StartPreparation(
        int index,
        ProgramSegment segment,
        IReadOnlyList<CornerTemplate> corners,
        IReadOnlyList<DjProfile> djs,
        string songContext,
        IReadOnlyList<string> castDjIds,
        int firstTalkIndex,
        string? greeting,
        string journalContext,
        string? guestCornerId,
        DjProfile? guest,
        ArtistProfile? artist,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Talk:
                // corner は preflight 済み（必ず見つかる）。準備（LLM 台本 + 全行 TTS）を裏で起動。
                var corner = corners.First(c => c.Id == segment.CornerId);
                // ゲストコーナー（W14）はゲストを cast 末尾へ。判定はゲスト分岐を先に（万一ゲストが最初の talk でも挨拶を付けない）。
                // それ以外は冒頭トークにのみ挨拶＋振り返り、他コーナーは時報リード文（W13.5 §3 / W18 §6）。cast は当日編成（先頭＝メイン）。
                CornerContext context;
                if (guestCornerId is not null && segment.CornerId == guestCornerId)
                {
                    context = new CornerContext(CastDjIds: castDjIds, Greeting: null, LeadIn: corner.LeadIn, Guest: guest);
                }
                else
                {
                    var isFirstTalk = index == firstTalkIndex;
                    context = new CornerContext(
                        CastDjIds: castDjIds,
                        Greeting: isFirstTalk ? greeting : null,
                        LeadIn: isFirstTalk ? null : corner.LeadIn,
                        JournalContext: isFirstTalk ? journalContext : null);
                }
                var talkToken = ledger.BeginPreparation(index, ct);
                ledger.AddCorner(index, PrepareCornerAsync(corner, djs, context, index, ledger, talkToken));
                break;

            case SegmentKind.Song:
                var spec = segment.Song!;
                var songToken = ledger.BeginPreparation(index, ct);
                ledger.AddSong(index, PickSongAsync(spec, songContext, songToken));
                break;

            case SegmentKind.News:
                // 出現のたびに生成（長時間放送でニュースが更新される）。
                var newsToken = ledger.BeginPreparation(index, ct);
                ledger.AddNews(index, _newsAnnouncement(newsToken));
                break;

            case SegmentKind.ArtistFeature:
                // アーティスト特集（W15）。重い準備（top-tracks + 複数台本 + 全行 TTS）をローリング窓で先行起動。
                // 直前の長いゲスト talk がバッファになり窓 2 で間に合う（§8-3）。lead-in は corner から直接渡す（CornerContext は talk 専用）。
                var featureCorner = corners.First(c => c.Id == segment.CornerId);
                var featureToken = ledger.BeginPreparation(index, ct);
                ledger.AddArtistFeature(
                    index, PrepareArtistFeatureAsync(featureCorner, artist, djs, castDjIds, featureCorner.LeadIn, featureToken));
                break;

            case SegmentKind.Opening:
            case SegmentKind.Ending:
                break; // 準備なし
        }
    }

    /// <summary>アーティスト特集の準備をラップ（runner 未配線は fail-fast）。準備は無音なので失敗時の pause は不要。</summary>
    private Task<PreparedArtistFeature> PrepareArtistFeatureAsync(
        CornerTemplate corner, ArtistProfile? artist, IReadOnlyList<DjProfile> djs,
        IReadOnlyList<string> castDjIds, string? leadIn, CancellationToken ct)
    {
        if (_artistFeatureRunner is null)
        {
            throw ConfigException.MissingField("artist_feature 有効だが ArtistFeatureEngine が未配線です");
        }
        return _artistFeatureRunner.PrepareAsync(corner, artist, djs, castDjIds, leadIn, ct);
    }

    /// <summary>
    /// トーク準備をラップし、<see cref="CornerEngine.PrepareAsync"/> が**成功完了した後にのみ**
    /// <see cref="PreparationLedger.MarkCornerPrepared"/> する（Mac `BroadcastEngine.swift` の mark-after-prepare 移植）。
    /// キャンセル / 失敗ではマークしない（「ED で終了」で未完了/faulted なトークを誤って流さないため）。
    /// </summary>
    private async Task<PreparedCorner> PrepareCornerAsync(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, CornerContext context, int index, PreparationLedger ledger, CancellationToken ct)
    {
        var prepared = await _cornerRunner.PrepareAsync(corner, djs, context, ct).ConfigureAwait(false);
        ledger.MarkCornerPrepared(index);
        return prepared;
    }

    /// <summary>番組内で最初の talk セグメントの index（冒頭挨拶を付ける対象。通常 2。無ければ -1）。Mac `firstTalkIndex` 移植。</summary>
    private static int FirstTalkIndex(ProgramPlan plan)
    {
        var index = 0;
        while (plan.Segment(index) is ProgramSegment seg)
        {
            if (seg.Kind == SegmentKind.Talk)
            {
                return index;
            }
            index++;
            if (index > 8)
            {
                break; // 安全弁: 冒頭付近にあるはず（無ければ talk なし番組。エンドレスの無限ループ回避も兼ねる）
            }
        }
        return -1;
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
        DjProfile main,
        IReadOnlyDictionary<string, string> extraValues,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        _onEvent?.Invoke(new BroadcastEvent.SegmentStarted(index, segment.Kind));
        try
        {
            await PerformAsync(index, segment, themes, djs, anchor, main, extraValues, ledger, ct).ConfigureAwait(false);
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
        DjProfile main,
        IReadOnlyDictionary<string, string> extraValues,
        PreparationLedger ledger,
        CancellationToken ct)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Opening:
            {
                // その日のメインが読み、メインの口上を使う（無ければ anchor → 先頭の順。W13.5 §7）。
                var opSpiel = themes.Opening.Spiel(main.Id, new[] { anchor.Id }) ?? new DjSpiel("");
                var opStaging = themes.Opening.Staging with { Tagline = opSpiel.Tagline };
                await _themeSequencer
                    .RunAsync(opStaging, Expand(themes.Greetings, opSpiel.Announcement, extraValues), main.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;
            }

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
            {
                // ED もその日のメインが読み、メインの口上を使う（tagline なし）。無ければ anchor → 先頭（W13.5 §7）。
                var edSpiel = themes.Ending.Spiel(main.Id, new[] { anchor.Id }) ?? new DjSpiel("");
                var edStaging = themes.Ending.Staging with { Tagline = edSpiel.Tagline };
                await _themeSequencer
                    .RunAsync(edStaging, Expand(themes.Greetings, edSpiel.Announcement, extraValues), main.SpeakerId, ct)
                    .ConfigureAwait(false);
                break;
            }

            case SegmentKind.ArtistFeature:
            {
                // アーティスト特集（W15）。先行準備した PreparedArtistFeature を実行。スキップ（プール空 / K≤2）は
                // ランナーが内部で FeatureSkipped を出して何も流さない（SegmentFailed には載せない＝§8-4）。
                var featureTask = ledger.ArtistFeatureTask(index)
                    ?? throw BroadcastException.SegmentFailed($"artist_feature: 準備タスクがありません（index {index}）");
                var preparedFeature = await featureTask.ConfigureAwait(false);
                if (_artistFeatureRunner is null)
                {
                    throw BroadcastException.SegmentFailed($"artist_feature: ランナー未配線（index {index}）");
                }
                await _artistFeatureRunner.RunAsync(preparedFeature, djs, ct).ConfigureAwait(false);
                break;
            }
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
    /// ローリング準備の台帳（仕様 s13/w13 §3-3）。準備 Task（選曲 / 台本 / ニュース原稿）と、それぞれを止める
    /// **セグメント index ごとの linked CTS** を保持し、消費・破棄・選択的キャンセルをスレッドセーフに行う
    /// （準備は並行に完了し、キャンセルは停止 / 「ED で終了」から飛んでくる）。
    /// <para>CTS のライフサイクル契約: <see cref="Discard"/> と <see cref="DrainAsync"/> は **lock 下で辞書から
    /// エントリを除去してから** CTS を Dispose する（後続の <see cref="CancelAll"/> が Dispose 済み CTS を
    /// Cancel して <c>ObjectDisposedException</c> にならないため）。<see cref="DrainAsync"/> は Task を
    /// await（観測）してから CTS を Dispose する（実行中 Task が観測中のトークンを先に破棄しない）。</para>
    /// </summary>
    private sealed class PreparationLedger
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, Task<TrackInfo>> _songTasks = new();
        private readonly Dictionary<int, Task<PreparedCorner>> _cornerTasks = new();
        private readonly Dictionary<int, Task<string>> _newsTasks = new();
        private readonly Dictionary<int, Task<PreparedArtistFeature>> _artistFeatureTasks = new();
        private readonly Dictionary<int, CancellationTokenSource> _ctsByIndex = new();
        private readonly HashSet<int> _preparedCorners = new();

        /// <summary>index 用の準備キャンセル源（エンジン ct に連動）を作り token を返す。</summary>
        public CancellationToken BeginPreparation(int index, CancellationToken engineCt)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(engineCt);
            lock (_lock) { _ctsByIndex[index] = cts; }
            return cts.Token;
        }

        public void AddSong(int index, Task<TrackInfo> task) { lock (_lock) { _songTasks[index] = task; } }
        public void AddCorner(int index, Task<PreparedCorner> task) { lock (_lock) { _cornerTasks[index] = task; } }
        public void AddNews(int index, Task<string> task) { lock (_lock) { _newsTasks[index] = task; } }
        public void AddArtistFeature(int index, Task<PreparedArtistFeature> task) { lock (_lock) { _artistFeatureTasks[index] = task; } }

        public Task<TrackInfo>? SongTask(int index) { lock (_lock) { return _songTasks.GetValueOrDefault(index); } }
        public Task<PreparedCorner>? CornerTask(int index) { lock (_lock) { return _cornerTasks.GetValueOrDefault(index); } }
        public Task<string>? NewsTask(int index) { lock (_lock) { return _newsTasks.GetValueOrDefault(index); } }
        public Task<PreparedArtistFeature>? ArtistFeatureTask(int index) { lock (_lock) { return _artistFeatureTasks.GetValueOrDefault(index); } }

        /// <summary>トーク準備が**成功完了**したことを記録（ED 判定の「直後トークが準備済みか」用）。</summary>
        public void MarkCornerPrepared(int index) { lock (_lock) { _preparedCorners.Add(index); } }

        /// <summary>準備が完了済みか（ED 判定用。実行中・未着手・失敗は false）。</summary>
        public bool IsCornerPrepared(int index) { lock (_lock) { return _preparedCorners.Contains(index); } }

        /// <summary>
        /// <paramref name="keeping"/> 以外の生存（未除去）index の準備をキャンセルする（Mac <c>cancelAll(keeping:)</c>）。
        /// Cancel は lock 外で行う（Cancel が登録コールバックを同期実行しうるためデッドロックを避ける）。
        /// </summary>
        public void CancelAll(int? keeping = null)
        {
            List<CancellationTokenSource> toCancel;
            lock (_lock)
            {
                toCancel = new List<CancellationTokenSource>(_ctsByIndex.Count);
                foreach (var (index, cts) in _ctsByIndex)
                {
                    if (index != keeping) { toCancel.Add(cts); }
                }
            }
            foreach (var cts in toCancel)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* 既に破棄済み = no-op */ }
            }
        }

        /// <summary>消費済みの準備を破棄する（ローリング窓の後端）。辞書から除去してから CTS を Dispose。</summary>
        public void Discard(int index)
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                _songTasks.Remove(index);
                _cornerTasks.Remove(index);
                _newsTasks.Remove(index);
                _artistFeatureTasks.Remove(index);
                _preparedCorners.Remove(index);
                _ctsByIndex.Remove(index, out cts);
            }
            cts?.Dispose();
        }

        /// <summary>
        /// 保持中（未消費）の準備 Task をすべて観測し（キャンセル後の未観測例外を防ぐ）、その後 CTS を Dispose する。
        /// 辞書は lock 下で抜き取ってクリアするため、二重 Drain は no-op。
        /// </summary>
        public async Task DrainAsync()
        {
            List<Task> tasks;
            List<CancellationTokenSource> ctsList;
            lock (_lock)
            {
                tasks = new List<Task>(_songTasks.Count + _cornerTasks.Count + _newsTasks.Count + _artistFeatureTasks.Count);
                tasks.AddRange(_songTasks.Values);
                tasks.AddRange(_cornerTasks.Values);
                tasks.AddRange(_newsTasks.Values);
                tasks.AddRange(_artistFeatureTasks.Values);
                ctsList = new List<CancellationTokenSource>(_ctsByIndex.Values);
                _songTasks.Clear();
                _cornerTasks.Clear();
                _newsTasks.Clear();
                _artistFeatureTasks.Clear();
                _preparedCorners.Clear();
                _ctsByIndex.Clear();
            }
            foreach (var task in tasks)
            {
                try { await task.ConfigureAwait(false); }
                catch { /* 破棄された準備（キャンセル/失敗）。ここでは観測のみ。 */ }
            }
            foreach (var cts in ctsList) { cts.Dispose(); }
        }
    }
}
