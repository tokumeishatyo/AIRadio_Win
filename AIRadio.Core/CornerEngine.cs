namespace AIRadio.Core;

/// <summary>コーナー進行中の出来事（デモ表示・ログ用）。W6 は free_talk の範囲。</summary>
public abstract record CornerEvent
{
    public sealed record ThemeSelected(string Theme) : CornerEvent;

    /// <summary>お便りコーナー（W12）で架空リスナーのお便りを生成した（ラジオネーム）。</summary>
    public sealed record LetterReady(string RadioName) : CornerEvent;

    /// <summary>ゲストコーナー（W14）で迎えるゲストが確定した（ゲスト名）。</summary>
    public sealed record GuestReady(string Name) : CornerEvent;

    public sealed record SongPicked(TrackInfo Track) : CornerEvent;

    public sealed record ScriptReady(int LineCount, int TotalCharacters) : CornerEvent;

    /// <summary>時報リード文（発話直前に時刻展開した実テキスト。W13.5 §3）。</summary>
    public sealed record LeadIn(string Text) : CornerEvent;

    public sealed record Line(DialogueLine DialogueLine) : CornerEvent;

    public sealed record SongStarted(TrackInfo Track) : CornerEvent;

    public sealed record SongFinished(TrackFinishReason Reason) : CornerEvent;
}

/// <summary>準備済みコーナー（LLM + TTS 処理の成果物。S10: 先行準備で生成し、本番で消費する）。</summary>
/// <param name="CastDjIds">実際に使った出演者（順序付き・先頭＝メイン。run で台本行→話者を一貫解決するため保持）。</param>
/// <param name="LeadIn">本編前に読む時報リード文テンプレート（時刻プレースホルダ含む。発話直前に展開。null/空＝頭出しなし。W13.5）。</param>
/// <param name="LeadInSpeakerId">時報リード文の読み手（その日のメイン）の speaker id（W13.5）。</param>
/// <param name="Guest">ゲストコーナー（W14）で迎えたゲスト（cast 末尾の出演者。<see cref="CastDjIds"/> には含めず別持ち＝djs に居ないため）。</param>
public sealed record PreparedCorner(
    CornerTemplate Corner,
    TrackInfo Song,
    DialogueScript Script,
    IReadOnlyList<byte[]> LineAudio,
    IReadOnlyList<string> CastDjIds,
    string? LeadIn = null,
    int LeadInSpeakerId = 0,
    DjProfile? Guest = null);

/// <summary>
/// コーナー 1 本の進行（W6: free_talk）。Mac 版 `CornerEngine` の移植（letter/guest/artist_feature・テーマプール・
/// リード文・日付/長期記憶は後続スライス）。**準備（LLM 処理、無音）と本番（発話 + 曲）の 2 段**:
/// 1. PrepareAsync: 選曲（プレフライト先行, §3-2）→ 台本生成（確定曲を締めで紹介）→ 全行を事前合成（本番の TTS 待ちゼロ, S10）
/// 2. RunAsync: 発話 → 一曲 → 必ず pause（§3-1）
/// 実差し替え境界ではない（design §2 に `CornerRunning` を含めない）ため具象クラス（§3-5）。
/// </summary>
public sealed class CornerEngine
{
    private readonly ILLMBackend _llm;
    private readonly ITTSBackend _tts;
    private readonly IAudioPlayer _audio;
    private readonly ITrackSearcher _searcher;
    private readonly ISpotifyController _spotify;
    private readonly IClock _clock;
    private readonly double _temperature;
    private readonly Action<CornerEvent>? _onEvent;
    /// <summary>テーマプールからの選択用乱数（count → 0..count-1 のインデックス。テストは決定論的に注入）。</summary>
    private readonly Func<int, int> _randomIndex;
    private readonly TimeZoneInfo _timeZone;
    /// <summary>曜日名・記念日の暦コンテキスト（W17。省略時は <see cref="DailyCalendar.Standard"/>）。</summary>
    private readonly DailyCalendar _calendar;

    /// <param name="randomIndex">テーマプール選択用の乱数（W12。省略時は <see cref="Random.Shared"/>）。</param>
    /// <param name="timeZone">日付・季節コンテキストの解釈に使うタイムゾーン（W12。省略時は <see cref="TimeZoneInfo.Local"/>）。</param>
    public CornerEngine(
        ILLMBackend llm,
        ITTSBackend tts,
        IAudioPlayer audio,
        ITrackSearcher searcher,
        ISpotifyController spotify,
        IClock clock,
        double temperature = 0.9,
        Action<CornerEvent>? onEvent = null,
        Func<int, int>? randomIndex = null,
        TimeZoneInfo? timeZone = null,
        DailyCalendar? calendar = null)
    {
        _llm = llm;
        _tts = tts;
        _audio = audio;
        _searcher = searcher;
        _spotify = spotify;
        _clock = clock;
        _temperature = temperature;
        _onEvent = onEvent;
        _randomIndex = randomIndex ?? (n => Random.Shared.Next(n));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _calendar = calendar ?? DailyCalendar.Standard;
    }

    /// <summary>準備 + 本番を続けて実行（単発デモ用）。</summary>
    public async Task RunAsync(CornerTemplate corner, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(corner, djs, ct: ct).ConfigureAwait(false);
        await RunAsync(prepared, djs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 準備（LLM 処理のみ。音を出さないため失敗時の pause も不要）。出演者は <paramref name="context"/> の
    /// CastDjIds（その日の編成・先頭＝メイン）で上書き、冒頭は挨拶、他は時報リード文（W13.5 §3）。
    /// </summary>
    public async Task<PreparedCorner> PrepareAsync(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, CornerContext? context = null, CancellationToken ct = default)
    {
        if (corner.Format is not (CornerFormat.FreeTalk or CornerFormat.Letter or CornerFormat.Guest))
        {
            // artist_feature（W15）は専用エンジンで実行する（CornerEngine には来ない想定）。
            throw ConfigException.MissingField($"{corner.Format} は CornerEngine では未対応（free_talk / letter / guest のみ）");
        }

        context ??= new CornerContext();
        // その日の編成（先頭＝メイン）で cast を解決。空/null なら corner 定義の DjIds（W13.5）。
        var castIds = context.CastDjIds is { Count: > 0 } ids ? ids : corner.DjIds;
        // ゲストコーナー（W14）はその日の編成の末尾にゲストを足す（cast に居ないため別持ち）。
        var guest = corner.Format == CornerFormat.Guest ? context.Guest : null;
        var cast = new List<DjProfile>(ResolveCast(castIds, djs));
        if (guest is not null)
        {
            cast.Add(guest);
            _onEvent?.Invoke(new CornerEvent.GuestReady(guest.Name));
        }
        var theme = SelectTheme(corner);
        _onEvent?.Invoke(new CornerEvent.ThemeSelected(theme));
        var dateContext = _calendar.Context(_clock.Now, _timeZone);

        var picker = new SongPicker(_llm, _searcher, _temperature);
        var generator = new DialogueScriptGenerator(_llm, _temperature);

        // letter: ①お便り生成 → ②リクエスト曲（お便り内容を選曲コンテキストに）→ ③台本（読み上げ + 感想 + 曲振り）。
        // free_talk: ①選曲 → ②台本。いずれも曲紹介テキストの生成より前に再生可否を確定する（§3-2）。
        ListenerLetter? letter = null;
        string songContext;
        if (corner.Format == CornerFormat.Letter)
        {
            letter = await new ListenerLetterGenerator(_llm, _temperature)
                .GenerateAsync(theme, dateContext, ct).ConfigureAwait(false);
            _onEvent?.Invoke(new CornerEvent.LetterReady(letter.RadioName));
            songContext = $"ラジオコーナー「{corner.Title}」でリスナーのお便りに応えてかけるリクエスト曲。お便りの内容: {letter.Body}";
        }
        else if (corner.Format == CornerFormat.Guest)
        {
            songContext = $"ラジオコーナー「{corner.Title}」（テーマ: {theme}）でゲストを迎えての会話の締めにかける曲";
        }
        else
        {
            songContext = $"ラジオコーナー「{corner.Title}」（テーマ: {theme}）の締めにかける曲";
        }

        var song = await picker.PickAsync(
            new SongRequest(songContext, corner.FallbackTrackUri, corner.SongPromptHint), ct).ConfigureAwait(false);
        _onEvent?.Invoke(new CornerEvent.SongPicked(song));

        var script = await generator.GenerateAsync(
            corner, cast, song, theme: theme, dateContext: dateContext, letter: letter,
            greeting: context.Greeting, guest: guest, journalContext: context.JournalContext ?? "", ct: ct).ConfigureAwait(false);
        _onEvent?.Invoke(new CornerEvent.ScriptReady(script.Lines.Count, script.TotalCharacters));

        // 全行を事前合成（本番の TTS 待ちをゼロに。準備は OP・冒頭曲の再生中に進む想定）。
        var lineAudio = new List<byte[]>(script.Lines.Count);
        foreach (var line in script.Lines)
        {
            lineAudio.Add(await _tts.SynthesizeAsync(line.Text, SpeakerIdFor(line, cast), ct).ConfigureAwait(false));
        }

        // 時報リード文は事前合成しない（時刻が再生時点でずれるため）。{theme}/{guest} は確定済みなので準備時に埋め、
        // 時刻プレースホルダのみ残して run で発話直前展開（W13.5 §3 / W14 §3）。
        var leadIn = string.IsNullOrEmpty(context.LeadIn) ? null : context.LeadIn;
        if (leadIn is not null)
        {
            leadIn = leadIn.Replace("{theme}", theme);
            if (guest is not null)
            {
                leadIn = leadIn.Replace("{guest}", guest.Name);
            }
        }
        return new PreparedCorner(
            corner, song, script, lineAudio, castIds, LeadIn: leadIn, LeadInSpeakerId: cast[0].SpeakerId, Guest: guest);
    }

    /// <summary>本番（発話 + 一曲。正常・例外・キャンセルいずれも最後は必ず pause）。</summary>
    public async Task RunAsync(PreparedCorner prepared, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        var castIds = prepared.CastDjIds.Count > 0 ? prepared.CastDjIds : prepared.Corner.DjIds;
        try
        {
            // ゲストは djs に居ないため prepared から末尾に補う（準備時と同じ並び。W14）。
            var cast = new List<DjProfile>(ResolveCast(castIds, djs));
            if (prepared.Guest is not null)
            {
                cast.Add(prepared.Guest);
            }
            await PerformAsync(prepared, cast, ct).ConfigureAwait(false);
        }
        catch
        {
            // 完全静寂（§3-1）: エラー / キャンセルでも必ず曲を止め、音量をフルに戻す。
            await _spotify.PauseIgnoringCancellationAsync(restoringVolume: prepared.Corner.Volume).ConfigureAwait(false);
            throw;
        }
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
    }

    private async Task PerformAsync(PreparedCorner prepared, IReadOnlyList<DjProfile> cast, CancellationToken ct)
    {
        // 0. 時報リード文（あれば）。発話直前に時刻を展開し、その場で合成＝再生時点で正確（W13.5 §3）。
        //    一過性のリード文失敗はスキップして本編へ（fail-tolerant）。キャンセルは握り潰さず伝播（§3-1）。
        if (!string.IsNullOrEmpty(prepared.LeadIn))
        {
            var values = TimePhrases.Values(_clock.Now, null, _timeZone);
            var text = TemplateExpander.Expand(prepared.LeadIn, values);
            _onEvent?.Invoke(new CornerEvent.LeadIn(text));
            try
            {
                var wav = await _tts.SynthesizeAsync(text, prepared.LeadInSpeakerId, ct).ConfigureAwait(false);
                await _audio.PlayAsync(wav, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                // 一過性のリード文失敗はスキップして本編へ（リード文は付加要素）。
            }
        }

        // 1. 発話
        await SpeakAsync(prepared, cast, ct).ConfigureAwait(false);

        // 2. 一曲
        await _spotify.PlayAsync(prepared.Song.Uri, ct).ConfigureAwait(false);
        await _spotify.SetVolumeAsync(prepared.Corner.Volume, ct).ConfigureAwait(false);
        _onEvent?.Invoke(new CornerEvent.SongStarted(prepared.Song));
        if (prepared.Corner.PlaySeconds > 0)
        {
            await _clock.DelayAsync(prepared.Corner.PlaySeconds, ct).ConfigureAwait(false);
        }
        else
        {
            // 曲を終わりまで見届ける（早切り・無音の過走を防ぐ, S10 fix）。
            var reason = await _spotify.WaitForTrackToFinishAsync(prepared.Song.Uri, _clock, ct: ct).ConfigureAwait(false);
            _onEvent?.Invoke(new CornerEvent.SongFinished(reason));
        }
    }

    /// <summary>台本を 1 行ずつ再生する。事前合成済み音声があればそれを使い、なければ逐次合成する（互換パス）。</summary>
    private async Task SpeakAsync(PreparedCorner prepared, IReadOnlyList<DjProfile> cast, CancellationToken ct)
    {
        var lines = prepared.Script.Lines;
        if (prepared.LineAudio.Count == lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                _onEvent?.Invoke(new CornerEvent.Line(lines[i]));
                await _audio.PlayAsync(prepared.LineAudio[i], ct).ConfigureAwait(false);
            }
            return;
        }

        // 互換パス（事前合成なし）: 逐次合成して再生。
        foreach (var line in lines)
        {
            _onEvent?.Invoke(new CornerEvent.Line(line));
            var wav = await _tts.SynthesizeAsync(line.Text, SpeakerIdFor(line, cast), ct).ConfigureAwait(false);
            await _audio.PlayAsync(wav, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<DjProfile> ResolveCast(IReadOnlyList<string> ids, IReadOnlyList<DjProfile> djs)
    {
        var cast = new List<DjProfile>(ids.Count);
        foreach (var id in ids)
        {
            var dj = djs.FirstOrDefault(d => d.Id == id)
                ?? throw ConfigException.MissingField($"コーナーの出演 DJ が未定義: {id}");
            cast.Add(dj);
        }
        return cast;
    }

    /// <summary>テーマプールから 1 つ選ぶ（W12）。空/null なら <see cref="CornerTemplate.Theme"/> 固定。</summary>
    private string SelectTheme(CornerTemplate corner)
    {
        var pool = corner.ThemePool;
        if (pool is null || pool.Count == 0)
        {
            return corner.Theme;
        }
        return pool[_randomIndex(pool.Count)];
    }

    private static int SpeakerIdFor(DialogueLine line, IReadOnlyList<DjProfile> cast)
        => (cast.FirstOrDefault(d => d.Id == line.DjId) ?? cast[0]).SpeakerId;
}
