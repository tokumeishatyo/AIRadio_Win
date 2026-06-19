using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// speaker_id で VOICEVOX / AquesTalk を振り分ける <see cref="ITTSBackend"/> 合成（W-AQT）。
/// マップにある speaker_id は AquesTalk（声種別 DLL）、それ以外は VOICEVOX へ委譲する。エンジン・<c>NAudioPlayer</c>・
/// 既存エンジンは無改修で、本クラスを composition で `VoicevoxTTS` の代わりに注入する。
/// AquesTalk エンジンが初期化失敗（DLL/辞書不在）で <c>null</c> の場合、該当 DJ は無音 WAV で放送を継続する
/// （fail-tolerant・spec §5-3）。
/// </summary>
public sealed class RoutingTTS : ITTSBackend
{
    private readonly ITTSBackend _voicevox;
    private readonly AquesTalk1Synthesizer? _aquestalk;
    private readonly IReadOnlyDictionary<int, string> _voiceBySpeakerId;
    private readonly Action<string>? _warnOnce;
    private int _warned;

    /// <param name="voicevox">VOICEVOX backend（既定経路）。</param>
    /// <param name="aquestalk">AquesTalk 合成器（初期化失敗時 <c>null</c>＝該当 DJ 無音）。</param>
    /// <param name="voiceBySpeakerId">speaker_id → 声種（<see cref="BuildVoiceMap"/> で構築・検証済み）。</param>
    /// <param name="warnOnce">AquesTalk エンジン不在時に一度だけ呼ぶ警告ログ（任意）。</param>
    public RoutingTTS(
        ITTSBackend voicevox,
        AquesTalk1Synthesizer? aquestalk,
        IReadOnlyDictionary<int, string> voiceBySpeakerId,
        Action<string>? warnOnce = null)
    {
        ArgumentNullException.ThrowIfNull(voicevox);
        ArgumentNullException.ThrowIfNull(voiceBySpeakerId);
        _voicevox = voicevox;
        _aquestalk = aquestalk;
        _voiceBySpeakerId = voiceBySpeakerId;
        _warnOnce = warnOnce;
    }

    public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
    {
        if (_voiceBySpeakerId.TryGetValue(speakerId, out var voiceId))
        {
            if (_aquestalk is not null)
            {
                return _aquestalk.SynthesizeAsync(voiceId, text, ct);
            }

            // エンジン初期化失敗（DLL/辞書不在）→ 当該 DJ は無音で放送継続（fail-tolerant・spec §5-3）。
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _warnOnce?.Invoke(
                    $"AquesTalk エンジンが利用できないため speaker_id={speakerId}（声種 {voiceId}）は無音で継続します。");
            }
            return Task.FromResult(AquesTalk1Interop.EmptyWav());
        }

        return _voicevox.SynthesizeAsync(text, speakerId, ct);
    }

    /// <summary>
    /// DJ/ゲスト一覧から <c>speaker_id → 声種</c> マップを構築する（W-AQT・spec §5-5）。
    /// <c>aquestalk</c> な DJ の speaker_id は他の全 DJ と一意でなければならない（ルーティングキー兼用）。
    /// <c>Enumerable.ToDictionary</c> は重複キーで <c>ArgumentException</c> を投げ、
    /// composition の <c>catch(RadioException)</c> を素通りして未捕捉クラッシュになるため、materialize の前に検証する（WAQT-3）。
    /// VOICEVOX 同士の speaker_id 重複は許容する（同一バックエンドへ振り分くだけ）。
    /// </summary>
    /// <exception cref="ConfigException">aquestalk speaker_id が重複、または VOICEVOX の speaker_id と衝突。</exception>
    public static IReadOnlyDictionary<int, string> BuildVoiceMap(IEnumerable<DjProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var voicevoxIds = new HashSet<int>();
        var aquestalk = new List<DjProfile>();
        foreach (var p in profiles)
        {
            if (string.Equals(p.TtsBackend, "aquestalk", StringComparison.Ordinal))
            {
                aquestalk.Add(p);
            }
            else
            {
                voicevoxIds.Add(p.SpeakerId);
            }
        }

        var map = new Dictionary<int, string>();
        foreach (var p in aquestalk)
        {
            if (string.IsNullOrEmpty(p.AquesTalkVoice))
            {
                throw ConfigException.MissingField($"djs/guests[{p.Id}].aquestalk_voice");
            }
            if (map.ContainsKey(p.SpeakerId) || voicevoxIds.Contains(p.SpeakerId))
            {
                throw ConfigException.MissingField(
                    $"aquestalk speaker_id={p.SpeakerId}（一意性違反: 他 DJ と衝突。AquesTalk DJ は一意の speaker_id にしてください）");
            }
            map[p.SpeakerId] = p.AquesTalkVoice;
        }
        return map;
    }
}
