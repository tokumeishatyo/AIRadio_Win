using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>AquesTalk1 ネイティブ層の共有コントラクト定数（W-AQT）。</summary>
public static class AquesTalk1Interop
{
    /// <summary>
    /// DLL ロード失敗を表す予約 errCode。AquesTalk の合成エラー（NULL 時の pSize 100-204）や
    /// AqKanji2Koe_Create のエラーコードと衝突しない負値にし、ロード失敗を合成失敗と区別する（spec §4-1・T03）。
    /// </summary>
    public const int LoadFailedErrorCode = -1000;

    /// <summary>空テキスト／無音フォールバック用の最小 WAV（44 byte・data 0・8kHz/16bit/mono）。</summary>
    internal static byte[] EmptyWav()
    {
        const int sampleRate = 8000, channels = 1, bitsPerSample = 16, dataSize = 0;
        const int byteRate = sampleRate * channels * (bitsPerSample / 8);
        const int blockAlign = channels * (bitsPerSample / 8);
        var bytes = new byte[44];

        bytes[0] = (byte)'R'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F'; bytes[3] = (byte)'F';
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        bytes[8] = (byte)'W'; bytes[9] = (byte)'A'; bytes[10] = (byte)'V'; bytes[11] = (byte)'E';
        bytes[12] = (byte)'f'; bytes[13] = (byte)'m'; bytes[14] = (byte)'t'; bytes[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)channels).CopyTo(bytes, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(bytes, 24);
        BitConverter.GetBytes(byteRate).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)bitsPerSample).CopyTo(bytes, 34);
        bytes[36] = (byte)'d'; bytes[37] = (byte)'a'; bytes[38] = (byte)'t'; bytes[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);

        return bytes;
    }
}

/// <summary>
/// AquesTalk1.0 / AqKanji2Koe の P/Invoke を隠すテスト seam（W-AQT）。本番実装は <see cref="AquesTalk1Native"/>、
/// テストは fake を差し込み <see cref="AquesTalk1Synthesizer"/> のオーケストレーションだけを検証する。
/// </summary>
/// <remarks>
/// <c>errCode</c> 規約: <c>0</c>=成功 / <see cref="AquesTalk1Interop.LoadFailedErrorCode"/>=DLL ロード失敗 /
/// それ以外=合成・変換・生成の失敗（AquesTalk/AqKanji2Koe のネイティブエラーコード）。
/// </remarks>
public interface INativeAquesTalk1 : IDisposable
{
    /// <summary>AqKanji2Koe インスタンスを生成（辞書 dir 指定・dev key 設定）。失敗時 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr CreateKanjiToKoe(string dictDir, string? aqk2kDevKey, out int errCode);

    /// <summary>漢字かな交じり文 → 音声記号列（UTF-8）。失敗時 <c>errCode != 0</c> で空文字。</summary>
    string ConvertKanjiToKoe(IntPtr handle, string japaneseText, out int errCode);

    /// <summary>AqKanji2Koe インスタンスを解放。</summary>
    void ReleaseKanjiToKoe(IntPtr handle);

    /// <summary>音声記号列 → WAV（8kHz/16bit/mono・ヘッダ込み）。声種別 DLL を初回ロード。失敗時空配列＋<c>errCode</c>。</summary>
    byte[] Synthesize(string voiceId, string koe, int speed, out int errCode);
}

/// <summary>
/// AquesTalk1.0 による合成のオーケストレーション（W-AQT）。漢字かな交じり文を AqKanji2Koe で音声記号列へ変換し、
/// 声種別 DLL で 8kHz WAV を合成する。<see cref="ITTSBackend"/> は実装せず（speaker_id ではなく声種 id で選ぶため）、
/// <see cref="RoutingTTS"/> から呼ばれる具象クラス（§3-5・spec §4-2）。
/// </summary>
/// <remarks>
/// 実行時失敗は <see cref="TtsException"/>（AQT コード）を throw（VOICEVOX と同じ契約 → 既存エンジンの非 critical
/// セグメント握り潰しで放送継続）。ctor の AqKanji2Koe 生成失敗も throw し、composition 側で握り潰す（当該 DJ 無音・spec §5-3）。
/// </remarks>
public sealed class AquesTalk1Synthesizer : IDisposable
{
    private readonly INativeAquesTalk1 _native;
    private readonly int _speed;
    private readonly IntPtr _kanjiToKoeHandle;
    private bool _disposed;

    /// <param name="native">P/Invoke seam。本番は <see cref="AquesTalk1Native"/>、テストは fake。</param>
    /// <param name="dictDir">AqKanji2Koe 辞書ディレクトリ（aqdic.bin を含む）。</param>
    /// <param name="aqk2kDevKey">AqKanji2Koe 開発キー（空なら評価版＝漢字→koe 段でナ/マ行が「ヌ」化）。</param>
    /// <param name="speed">発話速度 [%] 50-300（100=標準）。</param>
    /// <exception cref="TtsException">AqKanji2Koe 生成失敗（ロード=LOAD / 辞書不正=INIT）。</exception>
    public AquesTalk1Synthesizer(INativeAquesTalk1 native, string dictDir, string? aqk2kDevKey, int speed = 100)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _speed = speed;

        _kanjiToKoeHandle = _native.CreateKanjiToKoe(dictDir, aqk2kDevKey, out var err);
        if (_kanjiToKoeHandle == IntPtr.Zero)
        {
            _native.Dispose();
            throw err == AquesTalk1Interop.LoadFailedErrorCode
                ? TtsException.AquesTalkLoadFailed($"AqKanji2Koe.dll（dictDir='{dictDir}'）")
                : TtsException.AquesTalkInitFailed($"AqKanji2Koe_Create err={err}, dictDir='{dictDir}'");
        }
    }

    /// <summary>音声記号列に変換して声種 <paramref name="voiceId"/> で合成し WAV バイト列を返す。</summary>
    public Task<byte[]> SynthesizeAsync(string voiceId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(voiceId);
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        // 空白のみ → 8kHz 空 WAV（AqKanji2Koe / AquesTalk を呼ばない）。
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(AquesTalk1Interop.EmptyWav());
        }

        // (1) 漢字かな交じり文 → 音声記号列。
        var koe = _native.ConvertKanjiToKoe(_kanjiToKoeHandle, text, out var convErr);
        if (convErr != 0)
        {
            throw TtsException.AquesTalkKanaFailed($"err={convErr}, text='{Truncate(text, 60)}'");
        }

        ct.ThrowIfCancellationRequested();

        // (2) 音声記号列 → WAV（声種別 DLL）。ロード失敗は合成失敗と区別する（T03）。
        var wav = _native.Synthesize(voiceId, koe, _speed, out var synErr);
        if (synErr == AquesTalk1Interop.LoadFailedErrorCode)
        {
            throw TtsException.AquesTalkLoadFailed($"voice='{voiceId}'");
        }
        if (synErr != 0 || wav.Length == 0)
        {
            throw TtsException.AquesTalkSynthesisFailed($"err={synErr}, voice='{voiceId}', koe='{Truncate(koe, 60)}'");
        }

        return Task.FromResult(wav);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _native.ReleaseKanjiToKoe(_kanjiToKoeHandle);
        _native.Dispose();
        _disposed = true;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
