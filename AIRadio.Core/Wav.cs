using System.Buffers.Binary;

namespace AIRadio.Core;

/// <summary>
/// WAV（RIFF/PCM）の最小限の解析・構築（W-OP 多声 OP の PCM ミックス用）。外部依存ゼロの純粋ロジック。
/// AquesTalk1 / VOICEVOX が返す WAV バイト列から PCM サンプルを取り出し、ミックス結果を再ラップする。
/// </summary>
public static class Wav
{
    /// <summary>解析した PCM（フォーマット + サンプル本体）。<see cref="Samples"/> は data チャンクの生バイト。</summary>
    public sealed record Pcm(int SampleRate, int Channels, int BitsPerSample, byte[] Samples);

    private const int HeaderSize = 44;

    /// <summary>
    /// RIFF チャンクを走査して fmt と data を取り出す（data チャンクが 44 byte 固定オフセット以外にあっても可＝堅牢）。
    /// 不正な WAV（RIFF/WAVE・fmt・data いずれか欠落）は <see cref="ArgumentException"/>。
    /// </summary>
    public static Pcm Parse(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length < 12 || !ChunkIs(wav, 0, 'R', 'I', 'F', 'F') || !ChunkIs(wav, 8, 'W', 'A', 'V', 'E'))
        {
            throw new ArgumentException("RIFF/WAVE ヘッダが不正です。", nameof(wav));
        }

        int? sampleRate = null, channels = null, bits = null;
        byte[]? data = null;

        // チャンクは 12 byte 目以降に並ぶ。各チャンク = 4 byte ID + 4 byte サイズ(LE) + 本体（奇数サイズは 1 byte パディング）。
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var body = pos + 8;
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0 || body + size > wav.Length)
            {
                size = wav.Length - body; // 宣言サイズが残量を超える場合は残量に丸める（堅牢性）。
            }

            if (ChunkIs(wav, pos, 'f', 'm', 't', ' ') && size >= 16)
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (ChunkIs(wav, pos, 'd', 'a', 't', 'a'))
            {
                data = new byte[size];
                Array.Copy(wav, body, data, 0, size);
            }

            pos = body + size + (size & 1); // 次チャンクへ（ワード境界アライン）。
        }

        if (sampleRate is null || channels is null || bits is null)
        {
            throw new ArgumentException("fmt チャンクがありません。", nameof(wav));
        }
        if (data is null)
        {
            throw new ArgumentException("data チャンクがありません。", nameof(wav));
        }
        return new Pcm(sampleRate.Value, channels.Value, bits.Value, data);
    }

    /// <summary>
    /// 44 byte ヘッダ + PCM 本体の WAV を構築する。<c>AquesTalk1Interop.EmptyWav()</c> と同一レイアウト
    /// （samples が空 + 8000/1/16 のとき byte 列まで一致）。
    /// </summary>
    public static byte[] BuildPcmWav(int sampleRate, int channels, int bitsPerSample, byte[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = channels * (bitsPerSample / 8);
        var dataSize = samples.Length;
        var bytes = new byte[HeaderSize + dataSize];

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
        if (dataSize > 0)
        {
            Array.Copy(samples, 0, bytes, HeaderSize, dataSize);
        }
        return bytes;
    }

    private static bool ChunkIs(byte[] w, int pos, char a, char b, char c, char d) =>
        w[pos] == (byte)a && w[pos + 1] == (byte)b && w[pos + 2] == (byte)c && w[pos + 3] == (byte)d;
}

/// <summary>
/// 複数声を 1 WAV に重ねる（同時発話）ときに、WAV フォーマットが一致しない場合に投げる内部シグナル。
/// 形式不一致を検出し throw・<b>エラーコード割り当てなし</b>（<see cref="RadioException"/> 派生ではない）。
/// 呼び出し側（<see cref="ThemeSequencer"/>）が捕捉して順次再生にフォールバックする（fail-tolerant・W-OP §3-4）。
/// </summary>
public sealed class WavFormatMismatchException : Exception
{
    public WavFormatMismatchException(string message) : base(message)
    {
    }
}

/// <summary>
/// PCM WAV を加算ミックスする純粋ユーティリティ（W-OP 同時発話）。全 WAV が同一フォーマット
/// （sampleRate / channels / 16bit）であることを要求し、int16 サンプルを飽和加算（クリップ）して
/// 長い方にゼロ詰めで揃える。フォーマット不一致は <see cref="WavFormatMismatchException"/>。
/// </summary>
public static class PcmWavMixer
{
    /// <summary>
    /// <paramref name="wavs"/>（前提: Count &gt;= 2。単一声は <see cref="ThemeSequencer"/> 側で Mix を経由しない）を
    /// 1 つの WAV に加算ミックスする。空リストは <see cref="ArgumentException"/>、フォーマット不一致 / 非 16bit は
    /// <see cref="WavFormatMismatchException"/>。
    /// </summary>
    public static byte[] Mix(IReadOnlyList<byte[]> wavs)
    {
        ArgumentNullException.ThrowIfNull(wavs);
        if (wavs.Count == 0)
        {
            throw new ArgumentException("ミックス対象の WAV がありません。", nameof(wavs));
        }

        var pcms = new Wav.Pcm[wavs.Count];
        for (var i = 0; i < wavs.Count; i++)
        {
            pcms[i] = Wav.Parse(wavs[i]);
        }

        var first = pcms[0];
        if (first.BitsPerSample != 16)
        {
            throw new WavFormatMismatchException($"16bit PCM のみ対応です（{first.BitsPerSample}bit）。");
        }
        foreach (var p in pcms)
        {
            if (p.SampleRate != first.SampleRate || p.Channels != first.Channels || p.BitsPerSample != first.BitsPerSample)
            {
                throw new WavFormatMismatchException(
                    $"WAV フォーマットが一致しません（{first.SampleRate}/{first.Channels}ch/{first.BitsPerSample}bit と " +
                    $"{p.SampleRate}/{p.Channels}ch/{p.BitsPerSample}bit）。");
            }
        }

        var maxBytes = 0;
        foreach (var p in pcms)
        {
            maxBytes = Math.Max(maxBytes, p.Samples.Length);
        }

        var sampleCount = maxBytes / 2; // 16bit = 2 byte/サンプル。
        var outBytes = new byte[sampleCount * 2];
        for (var s = 0; s < sampleCount; s++)
        {
            var offset = s * 2;
            var sum = 0;
            foreach (var p in pcms)
            {
                if (offset + 1 < p.Samples.Length)
                {
                    sum += BinaryPrimitives.ReadInt16LittleEndian(p.Samples.AsSpan(offset, 2));
                }
            }
            var clipped = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(offset, 2), clipped);
        }

        return Wav.BuildPcmWav(first.SampleRate, first.Channels, 16, outBytes);
    }
}
