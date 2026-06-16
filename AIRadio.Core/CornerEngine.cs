namespace AIRadio.Core;

/// <summary>コーナー進行中の出来事（デモ表示・ログ用）。W6 は free_talk の範囲。</summary>
public abstract record CornerEvent
{
    public sealed record ThemeSelected(string Theme) : CornerEvent;

    /// <summary>お便りコーナー（W12）で架空リスナーのお便りを生成した（ラジオネーム）。</summary>
    public sealed record LetterReady(string RadioName) : CornerEvent;

    public sealed record SongPicked(TrackInfo Track) : CornerEvent;

    public sealed record ScriptReady(int LineCount, int TotalCharacters) : CornerEvent;

    public sealed record Line(DialogueLine DialogueLine) : CornerEvent;

    public sealed record SongStarted(TrackInfo Track) : CornerEvent;

    public sealed record SongFinished(TrackFinishReason Reason) : CornerEvent;
}

/// <summary>準備済みコーナー（LLM + TTS 処理の成果物。S10: 先行準備で生成し、本番で消費する）。</summary>
public sealed record PreparedCorner(
    CornerTemplate Corner,
    TrackInfo Song,
    DialogueScript Script,
    IReadOnlyList<byte[]> LineAudio,
    IReadOnlyList<string> CastDjIds);

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
        TimeZoneInfo? timeZone = null)
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
    }

    /// <summary>準備 + 本番を続けて実行（単発デモ用）。</summary>
    public async Task RunAsync(CornerTemplate corner, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(corner, djs, ct).ConfigureAwait(false);
        await RunAsync(prepared, djs, ct).ConfigureAwait(false);
    }

    /// <summary>準備（LLM 処理のみ。音を出さないため失敗時の pause も不要）。</summary>
    public async Task<PreparedCorner> PrepareAsync(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        if (corner.Format is not (CornerFormat.FreeTalk or CornerFormat.Letter))
        {
            // guest（W14）/ artist_feature（W15）は本スライス未対応。
            throw ConfigException.MissingField($"{corner.Format} は CornerEngine では未対応（free_talk / letter のみ）");
        }

        var cast = ResolveCast(corner.DjIds, djs);
        var theme = SelectTheme(corner);
        _onEvent?.Invoke(new CornerEvent.ThemeSelected(theme));
        var dateContext = SeasonPhrases.DateContext(_clock.Now, _timeZone);

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
        else
        {
            songContext = $"ラジオコーナー「{corner.Title}」（テーマ: {theme}）の締めにかける曲";
        }

        var song = await picker.PickAsync(
            new SongRequest(songContext, corner.FallbackTrackUri, corner.SongPromptHint), ct).ConfigureAwait(false);
        _onEvent?.Invoke(new CornerEvent.SongPicked(song));

        var script = await generator.GenerateAsync(
            corner, cast, song, theme: theme, dateContext: dateContext, letter: letter, ct: ct).ConfigureAwait(false);
        _onEvent?.Invoke(new CornerEvent.ScriptReady(script.Lines.Count, script.TotalCharacters));

        // 全行を事前合成（本番の TTS 待ちをゼロに。準備は OP・冒頭曲の再生中に進む想定）。
        var lineAudio = new List<byte[]>(script.Lines.Count);
        foreach (var line in script.Lines)
        {
            lineAudio.Add(await _tts.SynthesizeAsync(line.Text, SpeakerIdFor(line, cast), ct).ConfigureAwait(false));
        }

        return new PreparedCorner(corner, song, script, lineAudio, corner.DjIds);
    }

    /// <summary>本番（発話 + 一曲。正常・例外・キャンセルいずれも最後は必ず pause）。</summary>
    public async Task RunAsync(PreparedCorner prepared, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        var castIds = prepared.CastDjIds.Count > 0 ? prepared.CastDjIds : prepared.Corner.DjIds;
        try
        {
            var cast = ResolveCast(castIds, djs);
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
