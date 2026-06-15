using System.Globalization;
using System.Text;

namespace AIRadio.Core;

/// <summary>
/// OP / ニュース / ED が共有する統一テーマ/BGM 演出。Mac 版 `ThemeSequencer` の移植。
/// `tagline → BGM フル音量 → intro 待機 → ducked へ降下 → 発話 → outro へシーク → フル音量へ戻し余韻 → 停止`。
/// 再生はアトミック（指定 URI をそのまま再生）である前提（Web API 再生, <see cref="ISpotifyController"/>）。
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

    public ThemeSequencer(ITTSBackend tts, IAudioPlayer audio, ISpotifyController spotify, IClock clock)
    {
        _tts = tts;
        _audio = audio;
        _spotify = spotify;
        _clock = clock;
    }

    public async Task RunAsync(ThemeConfig theme, string announcement, int speakerId, CancellationToken ct = default)
    {
        try
        {
            await SequenceAsync(theme, announcement, speakerId, ct).ConfigureAwait(false);
        }
        catch
        {
            // 完全静寂（§3-1）: エラー / キャンセルでも必ず BGM を止め、音量をフルに戻す。
            await _spotify.PauseIgnoringCancellationAsync(restoringVolume: theme.Volume).ConfigureAwait(false);
            throw;
        }
        await _spotify.PauseAsync(ct).ConfigureAwait(false);
    }

    private async Task SequenceAsync(ThemeConfig theme, string announcement, int speakerId, CancellationToken ct)
    {
        // tagline（BGM 前の一言、ED は null）。
        if (!string.IsNullOrEmpty(theme.Tagline))
        {
            var taglineWav = await _tts.SynthesizeAsync(theme.Tagline, speakerId, ct).ConfigureAwait(false);
            await _audio.PlayAsync(taglineWav, ct).ConfigureAwait(false);
        }

        // BGM イントロ（フル音量）。発話は文単位のチャンクに分け、最初のチャンクをイントロ中に先行合成する。
        // 長文（LLM ニュース原稿等）を一括合成するとダッキング後に合成待ちのデッドエアが出るため（S11 fix）。
        var chunks = ChunkAnnouncement(announcement);
        await _spotify.PlayAsync(theme.TrackUri, ct).ConfigureAwait(false);
        await _spotify.SetVolumeAsync(theme.Volume, ct).ConfigureAwait(false);

        // 先行合成（hot task）の未 await ハンドル。早期離脱（キャンセル/失敗）時に finally で観測し、
        // Swift の async let（暗黙キャンセル + await）相当の確定的な後始末にする（孤立した未観測タスクを残さない）。
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

            // アウトロ: 曲の「残り OutroSeconds 秒」へシーク → アンダック → 曲の終わりまで余韻。
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
        finally
        {
            // 未観測の先行合成があれば待って観測する（例外は破棄。ct シグナル済みなら即座に戻る）。
            if (inFlight is not null)
            {
                try { await inFlight.ConfigureAwait(false); }
                catch { /* 破棄された合成。ここでは観測のみ */ }
            }
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
        if (TrimHorizontal(current.ToString()).Length > 0)
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
            .Select(TrimHorizontal)
            .Where(c => c.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 水平方向の空白（スペース・タブ・Unicode Zs）だけを両端からトリムする。Swift の
    /// <c>CharacterSet.whitespaces</c> 相当で、改行類（LF/CR/VT/FF/NEL/LS/PS）は**保持**する
    /// （Mac 版が <c>.whitespacesAndNewlines</c> ではなく <c>.whitespaces</c> を使うのに合わせる）。
    /// </summary>
    private static string TrimHorizontal(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsHorizontalWhitespace(s[start]))
        {
            start++;
        }
        while (end > start && IsHorizontalWhitespace(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }

    /// <summary>Swift の <c>CharacterSet.whitespaces</c>（Unicode Zs カテゴリ + 水平タブ）に一致。改行類は含めない。</summary>
    private static bool IsHorizontalWhitespace(char c) =>
        c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
}
