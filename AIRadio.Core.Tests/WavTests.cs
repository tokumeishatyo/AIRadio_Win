using System.Buffers.Binary;
using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W-OP（多声 OP）の PCM ミックス基盤 <see cref="Wav"/> / <see cref="PcmWavMixer"/> の純粋ロジック検証。
/// 8kHz/16bit/mono を前提に、WAV 構築・解析（往復）・加算ミックス（クリップ/ゼロ詰め/形式不一致）を固定値で決定論的に検証する。
/// </summary>
public class WavTests
{
    /// <summary>16bit/mono の WAV を short サンプル列から組み立てる（テスト入力ビルダ）。</summary>
    private static byte[] Wav16(int sampleRate, params short[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
        }
        return Wav.BuildPcmWav(sampleRate, 1, 16, pcm);
    }

    private static short[] SamplesOf(byte[] wav)
    {
        var pcm = Wav.Parse(wav).Samples;
        var result = new short[pcm.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
        }
        return result;
    }

    // --- Wav.BuildPcmWav: 44 byte ヘッダのレイアウト（AquesTalk1Interop.EmptyWav と同一） ---

    [Fact]
    public void BuildPcmWav_EmptySamples_Produces44ByteCanonicalHeader()
    {
        var wav = Wav.BuildPcmWav(8000, 1, 16, Array.Empty<byte>());

        Assert.Equal(44, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36, BitConverter.ToInt32(wav, 4)); // 36 + dataSize(0)
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));       // fmt サイズ
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));        // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));        // channels
        Assert.Equal(8000, BitConverter.ToInt32(wav, 24));     // sampleRate
        Assert.Equal(16000, BitConverter.ToInt32(wav, 28));    // byteRate = 8000*1*2
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));        // blockAlign
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));       // bitsPerSample
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));        // dataSize
    }

    // --- Wav.Parse: 往復（フォーマット + サンプル byte 列まで保存） ---

    [Fact]
    public void Parse_RoundTrip_PreservesFormatAndSamples()
    {
        var samples = new byte[] { 1, 2, 3, 4, 0xFF, 0x7F, 0x00, 0x80 };
        var built = Wav.BuildPcmWav(8000, 1, 16, samples);

        var parsed = Wav.Parse(built);

        Assert.Equal(8000, parsed.SampleRate);
        Assert.Equal(1, parsed.Channels);
        Assert.Equal(16, parsed.BitsPerSample);
        Assert.Equal(samples, parsed.Samples); // byte-for-byte
    }

    // --- Wav.Parse: data チャンクが 44 byte 以外のオフセットにあっても取り出す（堅牢性） ---

    [Fact]
    public void Parse_DataChunkAfterExtraChunk_LocatesDataCorrectly()
    {
        // fmt の後に JUNK チャンク(8 byte 本体)を挟み、その後に data を置く手組み WAV。
        var data = new byte[] { 10, 20, 30, 40 };
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(0); // RIFF サイズ（本テストでは未使用）
        w.Write("WAVE"u8.ToArray());
        // fmt
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        // JUNK（パディング目的の余分なチャンク）
        w.Write("JUNK"u8.ToArray());
        w.Write(8);
        w.Write(new byte[8]);
        // data
        w.Write("data"u8.ToArray());
        w.Write(data.Length);
        w.Write(data);
        w.Flush();

        var parsed = Wav.Parse(ms.ToArray());

        Assert.Equal(8000, parsed.SampleRate);
        Assert.Equal(data, parsed.Samples);
    }

    [Fact]
    public void Parse_NotRiff_Throws()
    {
        Assert.Throws<ArgumentException>(() => Wav.Parse(new byte[44]));
    }

    [Fact]
    public void Parse_MissingFmtChunk_Throws()
    {
        // RIFF/WAVE + data はあるが fmt が無い。
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(0);
        w.Write("WAVE"u8.ToArray());
        w.Write("data"u8.ToArray());
        w.Write(4);
        w.Write(new byte[] { 1, 2, 3, 4 });
        w.Flush();

        Assert.Throws<ArgumentException>(() => Wav.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_MissingDataChunk_Throws()
    {
        // RIFF/WAVE + fmt はあるが data が無い。
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(0);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Flush();

        Assert.Throws<ArgumentException>(() => Wav.Parse(ms.ToArray()));
    }

    // --- PcmWavMixer.Mix: 加算 ---

    [Fact]
    public void Mix_SameLength_SumsSamples()
    {
        var a = Wav16(8000, 100, 200, 300);
        var b = Wav16(8000, 10, 20, 30);

        var mixed = SamplesOf(PcmWavMixer.Mix(new[] { a, b }));

        Assert.Equal(new short[] { 110, 220, 330 }, mixed);
    }

    [Fact]
    public void Mix_DifferentLength_ZeroPadsToLongest()
    {
        var a = Wav16(8000, 100, 200, 300);
        var b = Wav16(8000, 10); // 短い → 残りは無音(0)詰め

        var mixed = SamplesOf(PcmWavMixer.Mix(new[] { a, b }));

        Assert.Equal(new short[] { 110, 200, 300 }, mixed);
    }

    [Fact]
    public void Mix_Clips_ToInt16Bounds()
    {
        var a = Wav16(8000, 30000, -30000);
        var b = Wav16(8000, 30000, -30000);

        var mixed = SamplesOf(PcmWavMixer.Mix(new[] { a, b }));

        Assert.Equal(new short[] { short.MaxValue, short.MinValue }, mixed); // 60000→32767 / -60000→-32768
    }

    [Fact]
    public void Mix_ThreeVoices_SumsAll()
    {
        var a = Wav16(8000, 100);
        var b = Wav16(8000, 200);
        var c = Wav16(8000, 300);

        var mixed = SamplesOf(PcmWavMixer.Mix(new[] { a, b, c }));

        Assert.Equal(new short[] { 600 }, mixed);
    }

    [Fact]
    public void Mix_ThreeVoices_DifferentLengths_ZeroPadsToLongest()
    {
        var a = Wav16(8000, 100, 200);     // 2 サンプル
        var b = Wav16(8000, 50);           // 1 サンプル
        var c = Wav16(8000, 30, 40, 50);   // 3 サンプル（最長）

        var mixed = SamplesOf(PcmWavMixer.Mix(new[] { a, b, c }));

        // pos0: 100+50+30=180 / pos1: 200+0+40=240 / pos2: 0+0+50=50
        Assert.Equal(new short[] { 180, 240, 50 }, mixed);
    }

    [Fact]
    public void Mix_DifferentSampleRate_ThrowsFormatMismatch()
    {
        var a = Wav16(8000, 1, 2);
        var b = Wav16(16000, 3, 4);

        Assert.Throws<WavFormatMismatchException>(() => PcmWavMixer.Mix(new[] { a, b }));
    }

    [Fact]
    public void Mix_Non16Bit_ThrowsFormatMismatch()
    {
        // 同一フォーマット同士でも 8bit は非対応 → 形式不一致シグナル。
        var a = Wav.BuildPcmWav(8000, 1, 8, new byte[] { 1, 2 });
        var b = Wav.BuildPcmWav(8000, 1, 8, new byte[] { 3, 4 });

        Assert.Throws<WavFormatMismatchException>(() => PcmWavMixer.Mix(new[] { a, b }));
    }

    [Fact]
    public void Mix_EmptyList_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => PcmWavMixer.Mix(Array.Empty<byte[]>()));
    }

    [Fact]
    public void Mix_OutputIsValid8kHzMonoWav()
    {
        var mixed = Wav.Parse(PcmWavMixer.Mix(new[] { Wav16(8000, 1, 2), Wav16(8000, 3, 4) }));

        Assert.Equal(8000, mixed.SampleRate);
        Assert.Equal(1, mixed.Channels);
        Assert.Equal(16, mixed.BitsPerSample);
    }
}
