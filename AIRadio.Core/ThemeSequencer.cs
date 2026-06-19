using System.Text;

namespace AIRadio.Core;

/// <summary>
/// OP / ニュース / ED が共有する統一テーマ/BGM 演出。Mac 版 `ThemeSequencer` の移植。
/// `tagline → BGM フル音量 → intro 待機 → ducked へ降下 → 発話 → outro へシーク → フル音量へ戻し余韻 → 停止`。
/// 再生はアトミック（指定 URI をそのまま再生）である前提（Web API 再生, <see cref="ISpotifyController"/>）。
///
/// W-OP: 単一 announcement（string）に加え、多声台本（<see cref="SpokenStep"/> 列）のオーバーロードを持つ。
/// 同時発話ステップは各声を合成して PCM ミックス（<see cref="PcmWavMixer"/>）し 1 WAV で再生する。
///
/// 実差し替え境界ではない（実装 1 個）ため interface 化しない（CLAUDE.md §3-5）。テストは依存
/// （TTS / Audio / Spotify / Clock）を fake 注入して具象クラスのまま検証する。
/// </summary>
public sealed class ThemeSequencer
{
    private readonly ITTSBackend _tts;
    private readonly IAudioPlayer _audio;
    private readonly ISpotifyController _spotify;
    private readonly IClock _clock;
    private readonly Action<string>? _warn;

    public ThemeSequencer(ITTSBackend tts, IAudioPlayer audio, ISpotifyController spotify, IClock clock, Action<string>? warn = null)
    {
        _tts = tts;
        _audio = audio;
        _spotify = spotify;
        _clock = clock;
        _warn = warn;
    }

    /// <summary>単一話者・単一 announcement のテーマ進行（OP/ED/News の既存経路。W13.5）。</summary>
    public Task RunAsync(ThemeConfig theme, string announcement, int speakerId, CancellationToken ct = default) =>
        RunGuardedAsync(theme, () => SequenceAsync(theme, announcement, speakerId, ct), ct);

    /// <summary>多声台本のテーマ進行（W-OP）。<paramref name="taglineSpeakerId"/> は BGM 前 tagline の話者。</summary>
    public Task RunAsync(ThemeConfig theme, int taglineSpeakerId, IReadOnlyList<SpokenStep> steps, CancellationToken ct = default) =>
        RunGuardedAsync(theme, () => SequenceStepsAsync(theme, taglineSpeakerId, steps, ct), ct);

    /// <summary>完全静寂（§3-1）: 本体実行を try で包み、エラー / キャンセルでも必ず BGM を止めて音量をフルに戻す。</summary>
    private async Task RunGuardedAsync(ThemeConfig theme, Func<Task> sequence, CancellationToken ct)
    {
        try
        {
            await sequence().ConfigureAwait(false);
        }
        catch
        {
            await _spotify.PauseIgnoringCancellationAsync(restoringVolume: theme.Volume).ConfigureAwait(false);
            throw;
        }
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
    }

    private async Task SequenceAsync(ThemeConfig theme, string announcement, int speakerId, CancellationToken ct)
    {
        await PlayTaglineAsync(theme, speakerId, ct).ConfigureAwait(false);

        // BGM イントロ（フル音量）。発話は文単位のチャンクに分け、最初のチャンクをイントロ中に先行合成する。
        // 長文（LLM ニュース原稿等）を一括合成するとダッキング後に合成待ちのデッドエアが出るため（S11 fix）。
        var chunks = ChunkAnnouncement(announcement);
        await StartBgmFullAsync(theme, ct).ConfigureAwait(false);

        // 先行合成（hot task）の未 await ハンドル。早期離脱（キャンセル/失敗）時に finally で観測する。
        Task<byte[]>? inFlight = null;
        try
        {
            byte[]? pending = null;
            if (chunks.Count > 0)
            {
                inFlight = _tts.SynthesizeAsync(chunks[0], speakerId, ct); // 先行合成（イントロ中に裏で走らせる）
                await _clock.DelayAsync(theme.IntroSeconds, ct).ConfigureAwait(false);
                await _spotify.SetVolumeAsync(theme.DuckedVolume, ct).ConfigureAwait(false);
                pending = await inFlight.ConfigureAwait(false);
                inFlight = null;
            }
            else
            {
                await _clock.DelayAsync(theme.IntroSeconds, ct).ConfigureAwait(false);
                await _spotify.SetVolumeAsync(theme.DuckedVolume, ct).ConfigureAwait(false);
            }

            // 発話: 次のチャンクは現在チャンクの再生中に先行合成する（無音排除）。
            var index = 0;
            while (pending is not null)
            {
                var nextIndex = index + 1;
                if (nextIndex < chunks.Count)
                {
                    inFlight = _tts.SynthesizeAsync(chunks[nextIndex], speakerId, ct);
                    await _audio.PlayAsync(pending, ct).ConfigureAwait(false);
                    pending = await inFlight.ConfigureAwait(false);
                    inFlight = null;
                }
                else
                {
                    await _audio.PlayAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                }
                index = nextIndex;
            }

            await OutroAsync(theme, ct).ConfigureAwait(false);
        }
        finally
        {
            await ObserveInFlightAsync(inFlight).ConfigureAwait(false);
        }
    }

    private async Task SequenceStepsAsync(ThemeConfig theme, int taglineSpeakerId, IReadOnlyList<SpokenStep> steps, CancellationToken ct)
    {
        await PlayTaglineAsync(theme, taglineSpeakerId, ct).ConfigureAwait(false);
        await StartBgmFullAsync(theme, ct).ConfigureAwait(false);

        // 先行レンダ（合成 + ミックス）の未 await ハンドル（単一 announcement 経路と同型の先読み）。
        Task<IReadOnlyList<byte[]>>? inFlight = null;
        try
        {
            IReadOnlyList<byte[]>? pending = null;
            if (steps.Count > 0)
            {
                inFlight = RenderStepAsync(steps[0], 0, ct); // 先行レンダ（イントロ中に裏で走らせる）
                await _clock.DelayAsync(theme.IntroSeconds, ct).ConfigureAwait(false);
                await _spotify.SetVolumeAsync(theme.DuckedVolume, ct).ConfigureAwait(false);
                pending = await inFlight.ConfigureAwait(false);
                inFlight = null;
            }
            else
            {
                await _clock.DelayAsync(theme.IntroSeconds, ct).ConfigureAwait(false);
                await _spotify.SetVolumeAsync(theme.DuckedVolume, ct).ConfigureAwait(false);
            }

            var index = 0;
            while (pending is not null)
            {
                var nextIndex = index + 1;
                if (nextIndex < steps.Count)
                {
                    inFlight = RenderStepAsync(steps[nextIndex], nextIndex, ct);
                    await PlayRenderedAsync(pending, ct).ConfigureAwait(false);
                    pending = await inFlight.ConfigureAwait(false);
                    inFlight = null;
                }
                else
                {
                    await PlayRenderedAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                }
                index = nextIndex;
            }

            await OutroAsync(theme, ct).ConfigureAwait(false);
        }
        finally
        {
            await ObserveInFlightAsync(inFlight).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 1 ステップを再生用 WAV 列にレンダする。単独声→[WAV]、同時発話→[ミックス済み 1 WAV]。
    /// 形式不一致（VOICEVOX 混在等）は fail-tolerant: warn を出して順次再生（複数 WAV）へフォールバック（W-OP §3-4・Q2）。
    /// </summary>
    private async Task<IReadOnlyList<byte[]>> RenderStepAsync(SpokenStep step, int stepIndex, CancellationToken ct)
    {
        // step の全声を並行合成（RoutingTTS が speaker_id→声種を解決）。WhenAll は cancel で部分結果を返さない。
        var tasks = step.Voices.Select(v => _tts.SynthesizeAsync(v.Text, v.SpeakerId, ct)).ToArray();
        var wavs = await Task.WhenAll(tasks).ConfigureAwait(false);

        if (step.Voices.Count == 1)
        {
            return new[] { wavs[0] };
        }
        try
        {
            return new[] { PcmWavMixer.Mix(wavs) }; // 同時発話 = 1 WAV に合成
        }
        catch (WavFormatMismatchException ex)
        {
            // 不一致グループごとに 1 回 warn（グローバル 1 回ではない・W-OP §3-4）。
            _warn?.Invoke($"同時発話 step[{stepIndex}] の声フォーマットが不一致のため順次再生にフォールバックします: {ex.Message}");
            return wavs;
        }
    }

    private async Task PlayRenderedAsync(IReadOnlyList<byte[]> rendered, CancellationToken ct)
    {
        foreach (var wav in rendered)
        {
            await _audio.PlayAsync(wav, ct).ConfigureAwait(false);
        }
    }

    private async Task PlayTaglineAsync(ThemeConfig theme, int speakerId, CancellationToken ct)
    {
        // tagline（BGM 前の一言、ED は null）。
        if (!string.IsNullOrEmpty(theme.Tagline))
        {
            var taglineWav = await _tts.SynthesizeAsync(theme.Tagline, speakerId, ct).ConfigureAwait(false);
            await _audio.PlayAsync(taglineWav, ct).ConfigureAwait(false);
        }
    }

    private async Task StartBgmFullAsync(ThemeConfig theme, CancellationToken ct)
    {
        await _spotify.PlayAsync(theme.TrackUri, ct).ConfigureAwait(false);
        await _spotify.SetVolumeAsync(theme.Volume, ct).ConfigureAwait(false);
    }

    /// <summary>アウトロ: 曲の「残り OutroSeconds 秒」へシーク → アンダック（フル音量）→ 曲終わりまで余韻。</summary>
    private async Task OutroAsync(ThemeConfig theme, CancellationToken ct)
    {
        double duration;
        try
        {
            duration = await _spotify.CurrentTrackDurationSecondsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            duration = 0; // Mac の `try?` 相当。曲長が取れなければシークしない。
        }
        if (duration > theme.OutroSeconds)
        {
            // 四捨五入（Swift の Double.rounded() = 中点は 0 から遠い側へ）。C# 既定の銀行家丸めとは異なるため明示。
            var rounded = (int)Math.Round(duration, MidpointRounding.AwayFromZero);
            await _spotify.SeekAsync(rounded - theme.OutroSeconds, ct).ConfigureAwait(false);
        }
        await _spotify.SetVolumeAsync(theme.Volume, ct).ConfigureAwait(false); // アンダック（フル音量）
        await _clock.DelayAsync(theme.OutroSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>未観測の先行合成/レンダがあれば待って観測する（例外は破棄。ct シグナル済みなら即座に戻る）。両経路で共用。</summary>
    private static async Task ObserveInFlightAsync<T>(Task<T>? inFlight)
    {
        if (inFlight is not null)
        {
            try { await inFlight.ConfigureAwait(false); }
            catch { /* 破棄された先行処理。ここでは観測のみ */ }
        }
    }

    /// <summary>
    /// 発話文を文末（。！？!?）で区切り、<paramref name="maxChars"/> を目安に連結したチャンク列を返す。
    /// 1 チャンクずつ合成・再生し、次チャンクを再生中に先行合成するための分割。
    /// </summary>
    public static IReadOnlyList<string> ChunkAnnouncement(string text, int maxChars = 120)
    {
        const string sentenceEnders = "。！？!?";

        var sentences = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            current.Append(ch);
            if (sentenceEnders.Contains(ch))
            {
                sentences.Add(current.ToString());
                current.Clear();
            }
        }
        if (Whitespace.TrimHorizontal(current.ToString()).Length > 0)
        {
            sentences.Add(current.ToString());
        }

        var chunks = new List<string>();
        var buffer = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (buffer.Length > 0 && buffer.Length + sentence.Length > maxChars)
            {
                chunks.Add(buffer.ToString());
                buffer.Clear();
                buffer.Append(sentence);
            }
            else
            {
                buffer.Append(sentence);
            }
        }
        if (buffer.Length > 0)
        {
            chunks.Add(buffer.ToString());
        }

        return chunks
            .Select(Whitespace.TrimHorizontal)
            .Where(c => c.Length > 0)
            .ToList();
    }
}
