using System.Text.RegularExpressions;

namespace AIRadio.Core;

/// <summary>
/// アーティスト特集で特集する 1 組（<c>config/artists.yaml</c>。仕様 w15）。曲は持たず、実行時に top-tracks で確定する。
/// <paramref name="Reading"/>（カタカナ読み・VOICEVOX 辞書同期用。任意・既定 null＝読み未登録）は W19b で追加（後方互換）。
/// </summary>
public sealed record ArtistProfile(string Id, string Name, string? Reading = null);

/// <summary>
/// アーティスト特集の発話パート（台本生成の種別。仕様 w15 §7）。
/// Swift の連想値 enum を C# の <c>abstract record</c> + nested <c>sealed record</c> で表現する（<see cref="CornerEvent"/> と同型）。
/// </summary>
public abstract record ArtistFeaturePart
{
    /// <summary>導入（特集宣言＋アーティストへの思い一言）。</summary>
    public sealed record Intro : ArtistFeaturePart;

    /// <summary>
    /// グループの曲紹介（その曲を正確な曲名で紹介）。
    /// <paramref name="Index"/>（0 始まり）/ <paramref name="Total"/>（全グループ数）で連続感・ラスト明示を出し分ける。
    /// </summary>
    public sealed record GroupIntro(IReadOnlyList<TrackInfo> Tracks, int Index, int Total) : ArtistFeaturePart;

    /// <summary>感想（<paramref name="Shorter"/> = 2 回目以降の短い感想）。</summary>
    public sealed record Comment(bool Shorter) : ArtistFeaturePart;
}

/// <summary>アーティスト特集進行中の出来事（デモ表示・ログ用）。</summary>
public abstract record ArtistFeatureEvent
{
    public sealed record ArtistSelected(string Name) : ArtistFeatureEvent;

    public sealed record TracksPrepared(int Count) : ArtistFeatureEvent;

    public sealed record PartScriptReady(int LineCount, int TotalCharacters) : ArtistFeatureEvent;

    public sealed record LeadIn(string Text) : ArtistFeatureEvent;

    public sealed record Line(DialogueLine DialogueLine) : ArtistFeatureEvent;

    public sealed record SongStarted(TrackInfo Track) : ArtistFeatureEvent;

    public sealed record SongFinished(TrackFinishReason Reason) : ArtistFeatureEvent;

    /// <summary>特集をスキップ（プール空 or 再生可能曲不足）。Reason に安定コードを含む（仕様 w15 §8-4）。</summary>
    public sealed record FeatureSkipped(string Reason) : ArtistFeatureEvent;
}

/// <summary>
/// 準備済みアーティスト特集（LLM + 検索 + TTS の成果物。先行準備で生成し本番で消費。仕様 w15 §8-3）。
/// <see cref="Skipped"/> のときは本番で何も流さず <see cref="ArtistFeatureEvent.FeatureSkipped"/> を出す（プール空 or 曲不足）。
/// </summary>
/// <param name="Groups">縮約後の曲グループ（例 7 曲なら <c>[[t1,t2,t3],[t4,t5,t6],[t7]]</c>）。</param>
/// <param name="GroupIntroScripts">各グループの曲紹介台本（<paramref name="Groups"/> と同数・同順）。</param>
/// <param name="CommentScripts">各グループ後の感想台本（最後のグループの後は締めなので <c>Groups.Count - 1</c> 本）。</param>
/// <param name="OutroLine">固定の締め（LLM 生成しない。<c>{artist}</c> は準備時に実名展開済み）。</param>
/// <param name="LeadIn"><c>{artist}</c> 置換済み・時刻のみ残したリード文（発話直前に時刻展開）。</param>
public sealed record PreparedArtistFeature(
    CornerTemplate Corner,
    ArtistProfile? Artist,
    IReadOnlyList<IReadOnlyList<TrackInfo>> Groups,
    DialogueScript IntroScript,
    IReadOnlyList<byte[]> IntroAudio,
    IReadOnlyList<DialogueScript> GroupIntroScripts,
    IReadOnlyList<IReadOnlyList<byte[]>> GroupIntroAudio,
    IReadOnlyList<DialogueScript> CommentScripts,
    IReadOnlyList<IReadOnlyList<byte[]>> CommentAudio,
    DialogueLine OutroLine,
    byte[] OutroAudio,
    IReadOnlyList<string> CastDjIds,
    string? LeadIn,
    int LeadInSpeakerId,
    bool Skipped = false,
    string? SkipReason = null)
{
    /// <summary>スキップ用の準備物（プール空 or 曲不足。本番で FeatureSkipped を出して何も流さない）。</summary>
    public static PreparedArtistFeature Skip(CornerTemplate corner, IReadOnlyList<string> castDjIds, string reason) =>
        new(corner, Artist: null, Groups: Array.Empty<IReadOnlyList<TrackInfo>>(),
            IntroScript: new DialogueScript(Array.Empty<DialogueLine>()), IntroAudio: Array.Empty<byte[]>(),
            GroupIntroScripts: Array.Empty<DialogueScript>(), GroupIntroAudio: Array.Empty<IReadOnlyList<byte[]>>(),
            CommentScripts: Array.Empty<DialogueScript>(), CommentAudio: Array.Empty<IReadOnlyList<byte[]>>(),
            OutroLine: new DialogueLine("", ""), OutroAudio: Array.Empty<byte>(),
            CastDjIds: castDjIds, LeadIn: null, LeadInSpeakerId: 0,
            Skipped: true, SkipReason: reason);
}

/// <summary>
/// アーティスト特集の実行（仕様 w15）。<see cref="CornerEngine"/> と同じ「prepare（無音）/ run（発話 + 曲 + 必ず pause）」二段構成。
/// 単曲モデルの CornerEngine と違い <b>複数曲を連続再生</b>し、発話が複数パートに分かれる。
/// 実差し替え境界ではない（単一消費者）ため具象クラス（§3-5）。テストは collaborators（llm/tts/audio/catalog/spotify/clock）を fake 差し替えする。
/// </summary>
public sealed class ArtistFeatureEngine
{
    /// <summary>top-tracks に要求する曲数（多めに取って重複除外後に絞る）。</summary>
    public const int FetchLimit = 10;

    /// <summary>フル構成の曲数（3+3+1）。</summary>
    public const int TargetTracks = 7;

    /// <summary>特集を実施する最低曲数（下回るとスキップ。仕様 w15 §6）。</summary>
    public const int MinTracks = 3;

    private readonly ILLMBackend _llm;
    private readonly ITTSBackend _tts;
    private readonly IAudioPlayer _audio;
    private readonly IArtistCatalog _catalog;
    private readonly ISpotifyController _spotify;
    private readonly IClock _clock;
    private readonly double _temperature;
    private readonly TimeZoneInfo _timeZone;
    private readonly Action<ArtistFeatureEvent>? _onEvent;
    /// <summary>曜日名・記念日の暦コンテキスト（W17。省略時は <see cref="DailyCalendar.Standard"/>）。</summary>
    private readonly DailyCalendar _calendar;

    public ArtistFeatureEngine(
        ILLMBackend llm,
        ITTSBackend tts,
        IAudioPlayer audio,
        IArtistCatalog catalog,
        ISpotifyController spotify,
        IClock clock,
        double temperature = 0.9,
        TimeZoneInfo? timeZone = null,
        Action<ArtistFeatureEvent>? onEvent = null,
        DailyCalendar? calendar = null)
    {
        _llm = llm;
        _tts = tts;
        _audio = audio;
        _catalog = catalog;
        _spotify = spotify;
        _clock = clock;
        _temperature = temperature;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _onEvent = onEvent;
        _calendar = calendar ?? DailyCalendar.Standard;
    }

    // 準備 ------------------------------------------------------------------

    /// <summary>
    /// 準備: top-tracks 取得 → 重複除外 → 縮約確定 → パート別台本 → 事前合成。<paramref name="artist"/> が null ならスキップ準備物を返す。
    /// </summary>
    public async Task<PreparedArtistFeature> PrepareAsync(
        CornerTemplate corner, ArtistProfile? artist, IReadOnlyList<DjProfile> djs,
        IReadOnlyList<string> castDjIds, string? leadIn, CancellationToken ct = default)
    {
        var castIds = castDjIds.Count > 0 ? castDjIds : corner.DjIds;
        var cast = ResolveCast(castIds, djs);

        // プール空: スキップ準備物（仕様 w15 §8-4、E-ART-EMPTY-POOL-001）。
        if (artist is null)
        {
            return PreparedArtistFeature.Skip(corner, castIds, ArtistFeatureErrors.EmptyPoolReason());
        }
        _onEvent?.Invoke(new ArtistFeatureEvent.ArtistSelected(artist.Name));

        // 曲の確定（top-tracks → 重複除外 → 最大 7）。プレフライト先行（§3-2）。
        var fetched = await _catalog.TopTracksAsync(artist.Name, FetchLimit, ct).ConfigureAwait(false);
        var tracks = Deduplicate(fetched).Take(TargetTracks).ToList();
        _onEvent?.Invoke(new ArtistFeatureEvent.TracksPrepared(tracks.Count));
        if (tracks.Count < MinTracks)
        {
            return PreparedArtistFeature.Skip(
                corner, castIds, ArtistFeatureErrors.InsufficientTracksReason(tracks.Count, MinTracks));
        }
        var groups = SplitGroups(tracks);
        var prms = corner.ArtistFeatureParams ?? new ArtistFeatureParams();
        var dateContext = _calendar.Context(_clock.Now, _timeZone);

        // パート別台本（導入 → 各グループ紹介 → 各感想。締めは固定文）。
        var introScript = await GeneratePartAsync(
            new ArtistFeaturePart.Intro(), artist, cast, dateContext, prms.IntroTargetChars, ct).ConfigureAwait(false);
        var groupIntroScripts = new List<DialogueScript>();
        for (var index = 0; index < groups.Count; index++)
        {
            groupIntroScripts.Add(await GeneratePartAsync(
                new ArtistFeaturePart.GroupIntro(groups[index], index, groups.Count),
                artist, cast, dateContext, prms.GroupIntroTargetChars, ct).ConfigureAwait(false));
        }
        var commentScripts = new List<DialogueScript>();
        if (groups.Count >= 2)
        {
            for (var i = 0; i < groups.Count - 1; i++)
            {
                var shorter = i > 0; // 1 回目は長め、2 回目以降は短め（仕様 w15 §6）。
                commentScripts.Add(await GeneratePartAsync(
                    new ArtistFeaturePart.Comment(shorter), artist, cast, dateContext,
                    shorter ? prms.CommentShortTargetChars : prms.CommentTargetChars, ct).ConfigureAwait(false));
            }
        }
        // 締めの {artist} を準備時に実名へ展開（leadIn の {artist} 置換と同型。仕様 w15 §7）。
        var outroText = prms.OutroLine.Replace("{artist}", artist.Name);
        var outroLine = new DialogueLine(cast[0].Id, outroText);

        // 事前合成（本番の TTS 待ちゼロ。締めの固定文も合成）。
        var introAudio = await SynthesizeAsync(introScript, cast, ct).ConfigureAwait(false);
        var groupIntroAudio = new List<IReadOnlyList<byte[]>>();
        foreach (var script in groupIntroScripts)
        {
            groupIntroAudio.Add(await SynthesizeAsync(script, cast, ct).ConfigureAwait(false));
        }
        var commentAudio = new List<IReadOnlyList<byte[]>>();
        foreach (var script in commentScripts)
        {
            commentAudio.Add(await SynthesizeAsync(script, cast, ct).ConfigureAwait(false));
        }
        var outroAudio = await _tts.SynthesizeAsync(outroLine.Text, cast[0].SpeakerId, ct).ConfigureAwait(false);

        // リード文の {artist} を準備時に埋め、時刻プレースホルダのみ残す（発話直前に展開、仕様 w15 §4）。
        var leadInFilled = string.IsNullOrEmpty(leadIn) ? null : leadIn!.Replace("{artist}", artist.Name);

        return new PreparedArtistFeature(
            corner, artist, groups,
            introScript, introAudio,
            groupIntroScripts, groupIntroAudio,
            commentScripts, commentAudio,
            outroLine, outroAudio,
            castIds, leadInFilled, cast[0].SpeakerId);
    }

    private async Task<DialogueScript> GeneratePartAsync(
        ArtistFeaturePart part, ArtistProfile artist, IReadOnlyList<DjProfile> cast,
        string dateContext, int target, CancellationToken ct)
    {
        var request = DialogueScriptGenerator.MakeArtistFeatureRequest(
            part, artist.Name, cast, dateContext, target, _temperature);
        var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
        var script = DialogueScriptGenerator.Parse(raw, cast, minLines: 2);
        _onEvent?.Invoke(new ArtistFeatureEvent.PartScriptReady(script.Lines.Count, script.TotalCharacters));
        return script;
    }

    private async Task<IReadOnlyList<byte[]>> SynthesizeAsync(
        DialogueScript script, IReadOnlyList<DjProfile> cast, CancellationToken ct)
    {
        var audio = new List<byte[]>(script.Lines.Count);
        foreach (var line in script.Lines)
        {
            audio.Add(await _tts.SynthesizeAsync(line.Text, SpeakerIdFor(line, cast), ct).ConfigureAwait(false));
        }
        return audio;
    }

    // 本番 ------------------------------------------------------------------

    /// <summary>本番: 導入 → （グループ紹介 → 連続再生 → 感想）×G → 締め。正常・例外・キャンセルいずれも最後は必ず pause（§3-1）。</summary>
    public async Task RunAsync(PreparedArtistFeature prepared, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)
    {
        // スキップ（プール空 / 曲不足）は FeatureSkipped を出して何も流さない（仕様 w15 §8-4）。
        if (prepared.Skipped)
        {
            _onEvent?.Invoke(new ArtistFeatureEvent.FeatureSkipped(prepared.SkipReason ?? "スキップ"));
            return;
        }
        var castIds = prepared.CastDjIds.Count > 0 ? prepared.CastDjIds : prepared.Corner.DjIds;
        try
        {
            var cast = ResolveCast(castIds, djs);
            await PerformAsync(prepared, cast, ct).ConfigureAwait(false);
        }
        catch
        {
            // 完全静寂（§3-1）: どの曲・どの発話でキャンセル/失敗しても、最後に play した URI を止め音量を戻す。
            await _spotify.PauseIgnoringCancellationAsync(restoringVolume: prepared.Corner.Volume).ConfigureAwait(false);
            throw;
        }
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
    }

    private async Task PerformAsync(PreparedArtistFeature prepared, IReadOnlyList<DjProfile> cast, CancellationToken ct)
    {
        // 0. リード文（時刻を発話直前に展開）。一過性失敗はスキップ、キャンセルは伝播（§3-1）。
        if (!string.IsNullOrEmpty(prepared.LeadIn))
        {
            var values = TimePhrases.Values(_clock.Now, null, _timeZone);
            var text = TemplateExpander.Expand(prepared.LeadIn!, values);
            _onEvent?.Invoke(new ArtistFeatureEvent.LeadIn(text));
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

        // 1. 導入
        await SpeakAsync(prepared.IntroScript, prepared.IntroAudio, cast, ct).ConfigureAwait(false);

        // 2. （グループ紹介 → 連続再生 → 感想）×G。感想は最後のグループの後を除く。
        for (var index = 0; index < prepared.Groups.Count; index++)
        {
            await SpeakAsync(prepared.GroupIntroScripts[index], prepared.GroupIntroAudio[index], cast, ct).ConfigureAwait(false);
            await PlayGroupAsync(prepared.Groups[index], prepared.Corner, ct).ConfigureAwait(false);
            if (index < prepared.Groups.Count - 1 && index < prepared.CommentScripts.Count)
            {
                await SpeakAsync(prepared.CommentScripts[index], prepared.CommentAudio[index], cast, ct).ConfigureAwait(false);
            }
        }

        // 3. 締め（固定文）
        _onEvent?.Invoke(new ArtistFeatureEvent.Line(prepared.OutroLine));
        await _audio.PlayAsync(prepared.OutroAudio, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// グループ内を連続再生（曲間は pause を挟まずアトミック置換でシームレス）し、グループの最後に pause して次の発話へ。
    /// </summary>
    private async Task PlayGroupAsync(IReadOnlyList<TrackInfo> tracks, CornerTemplate corner, CancellationToken ct)
    {
        foreach (var track in tracks)
        {
            await _spotify.PlayAsync(track.Uri, ct).ConfigureAwait(false);
            await _spotify.SetVolumeAsync(corner.Volume, ct).ConfigureAwait(false);
            _onEvent?.Invoke(new ArtistFeatureEvent.SongStarted(track));
            if (corner.PlaySeconds > 0)
            {
                await _clock.DelayAsync(corner.PlaySeconds, ct).ConfigureAwait(false);
            }
            else
            {
                var reason = await _spotify.WaitForTrackToFinishAsync(track.Uri, _clock, ct: ct).ConfigureAwait(false);
                _onEvent?.Invoke(new ArtistFeatureEvent.SongFinished(reason));
            }
        }
        // グループ最終曲の後は音楽を止めてから次の発話（感想/グループ紹介/締め）に入る（曲被り防止、仕様 w15 §8）。
        // play_seconds>0 だと最終曲が鳴り続けるため必須。フル再生(=0)では自然終了済みで no-op。曲間（グループ内）では pause しない。
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
    }

    private async Task SpeakAsync(
        DialogueScript script, IReadOnlyList<byte[]> lineAudio, IReadOnlyList<DjProfile> cast, CancellationToken ct)
    {
        if (lineAudio.Count == script.Lines.Count)
        {
            for (var i = 0; i < script.Lines.Count; i++)
            {
                _onEvent?.Invoke(new ArtistFeatureEvent.Line(script.Lines[i]));
                await _audio.PlayAsync(lineAudio[i], ct).ConfigureAwait(false);
            }
            return;
        }
        // 互換: 事前合成がなければその場で合成。
        foreach (var line in script.Lines)
        {
            _onEvent?.Invoke(new ArtistFeatureEvent.Line(line));
            var wav = await _tts.SynthesizeAsync(line.Text, SpeakerIdFor(line, cast), ct).ConfigureAwait(false);
            await _audio.PlayAsync(wav, ct).ConfigureAwait(false);
        }
    }

    // ヘルパー --------------------------------------------------------------

    private static IReadOnlyList<DjProfile> ResolveCast(IReadOnlyList<string> ids, IReadOnlyList<DjProfile> djs)
    {
        var cast = new List<DjProfile>(ids.Count);
        foreach (var id in ids)
        {
            var dj = djs.FirstOrDefault(d => d.Id == id)
                ?? throw ConfigException.MissingField($"アーティスト特集の出演 DJ が未定義: {id}");
            cast.Add(dj);
        }
        if (cast.Count == 0)
        {
            throw ConfigException.MissingField("アーティスト特集の出演 DJ が空です");
        }
        return cast;
    }

    private static int SpeakerIdFor(DialogueLine line, IReadOnlyList<DjProfile> cast)
        => (cast.FirstOrDefault(d => d.Id == line.DjId) ?? cast[0]).SpeakerId;

    private static readonly Regex BracketVersionPattern = new(@"[\(（\[【].*?[\)）\]】]", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>同一 URI・同一の正規化タイトル（別バージョン）を除外する。</summary>
    public static IReadOnlyList<TrackInfo> Deduplicate(IReadOnlyList<TrackInfo> tracks)
    {
        var seenUri = new HashSet<string>();
        var seenTitle = new HashSet<string>();
        var result = new List<TrackInfo>();
        foreach (var track in tracks)
        {
            if (seenUri.Contains(track.Uri))
            {
                continue;
            }
            var key = CanonicalTitle(track.Title);
            if (key.Length > 0 && seenTitle.Contains(key))
            {
                continue;
            }
            seenUri.Add(track.Uri);
            if (key.Length > 0)
            {
                seenTitle.Add(key);
            }
            result.Add(track);
        }
        return result;
    }

    /// <summary>重複判定用の正規化タイトル（小文字化・括弧内のバージョン表記除去・空白除去）。</summary>
    public static string CanonicalTitle(string title)
    {
        var s = title.ToLowerInvariant();
        s = BracketVersionPattern.Replace(s, "");
        s = WhitespacePattern.Replace(s, "");
        return s;
    }

    /// <summary>曲数 K を 3+3+1 ベースのグループへ分割（縮約。仕様 w15 §6）。例 7→[3,3,1] 6→[3,3] 5→[3,2] 4→[3,1] 3→[3]。</summary>
    public static IReadOnlyList<IReadOnlyList<TrackInfo>> SplitGroups(IReadOnlyList<TrackInfo> tracks)
    {
        var count = tracks.Count;
        var front = Math.Min(3, count);
        var rem = count - front;
        var mid = Math.Min(3, rem);
        var last = rem - mid;
        var groups = new List<IReadOnlyList<TrackInfo>>();
        var i = 0;
        foreach (var size in new[] { front, mid, last })
        {
            if (size <= 0)
            {
                continue;
            }
            groups.Add(tracks.Skip(i).Take(size).ToList());
            i += size;
        }
        return groups;
    }
}
