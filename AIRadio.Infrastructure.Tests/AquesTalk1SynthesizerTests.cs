using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-AQT: AquesTalk1Synthesizer のオーケストレーション（fake native seam で検証）。</summary>
public class AquesTalk1SynthesizerTests
{
    private static AquesTalk1Synthesizer Build(FakeNativeAquesTalk1 native, int speed = 100)
        => new(native, "native/aqk2k/aq_dic", aqk2kDevKey: "DEV", speed);

    [Fact]
    public async Task Synthesize_ConvertsThenSynthesizes_InOrder_PassingVoiceAndSpeed()
    {
        var native = new FakeNativeAquesTalk1 { ConvertResult = "コエ", SynthResult = new byte[120] };
        using var synth = Build(native, speed: 130);

        var wav = await synth.SynthesizeAsync("f1", "こんにちは");

        Assert.Equal(new[] { "create", "convert", "synth" }, native.Calls);
        Assert.Equal("f1", native.LastVoiceId);
        Assert.Equal("コエ", native.LastKoe);
        Assert.Equal(130, native.LastSpeed);
        Assert.Equal(120, wav.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task Synthesize_EmptyOrWhitespace_ReturnsEmpty8kHzWav_WithoutConvertOrSynth(string text)
    {
        var native = new FakeNativeAquesTalk1();
        using var synth = Build(native);

        var wav = await synth.SynthesizeAsync("f1", text);

        Assert.Equal(44, wav.Length);                       // ヘッダのみ
        Assert.Equal(8000, BitConverter.ToInt32(wav, 24));  // SampleRate = 8000
        Assert.Equal(new[] { "create" }, native.Calls);     // convert/synth は呼ばない
    }

    [Fact]
    public async Task Synthesize_ConvertFails_ThrowsKanaFailed()
    {
        var native = new FakeNativeAquesTalk1 { ConvertErrCode = 106 };
        using var synth = Build(native);

        var ex = await Assert.ThrowsAsync<TtsException>(() => synth.SynthesizeAsync("f1", "漢字"));
        Assert.Equal("E-TTS-AQT-KANA-FAILED-001", ex.Code);
        Assert.DoesNotContain("synth", native.Calls);       // 合成へ進まない
    }

    [Fact]
    public async Task Synthesize_SyntheFails_ThrowsSynthesisFailed()
    {
        var native = new FakeNativeAquesTalk1 { SynthErrCode = 105 };  // 未定義音声記号 等
        using var synth = Build(native);

        var ex = await Assert.ThrowsAsync<TtsException>(() => synth.SynthesizeAsync("f1", "漢字"));
        Assert.Equal("E-TTS-AQT-SYNTHESIS-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Synthesize_VoiceDllLoadFails_ThrowsLoadFailed_NotSynthesis()
    {
        // T03: 声種DLLロード失敗（予約 errCode）は合成失敗と区別して LOAD-FAILED にマップ。
        var native = new FakeNativeAquesTalk1 { SynthErrCode = AquesTalk1Interop.LoadFailedErrorCode };
        using var synth = Build(native);

        var ex = await Assert.ThrowsAsync<TtsException>(() => synth.SynthesizeAsync("f1", "漢字"));
        Assert.Equal("E-TTS-AQT-LOAD-FAILED-001", ex.Code);
    }

    [Fact]
    public void Ctor_CreateReturnsZeroWithLoadCode_ThrowsLoadFailed()
    {
        var native = new FakeNativeAquesTalk1 { CreateErrCode = AquesTalk1Interop.LoadFailedErrorCode };

        var ex = Assert.Throws<TtsException>(() => Build(native));
        Assert.Equal("E-TTS-AQT-LOAD-FAILED-001", ex.Code);
        Assert.True(native.Disposed);   // 失敗時は native を破棄
    }

    [Fact]
    public void Ctor_CreateReturnsZeroWithOtherCode_ThrowsInitFailed()
    {
        var native = new FakeNativeAquesTalk1 { CreateErrCode = 7 };

        var ex = Assert.Throws<TtsException>(() => Build(native));
        Assert.Equal("E-TTS-AQT-INIT-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Synthesize_PreCancelled_ThrowsOperationCanceled_BeforeConvert()
    {
        var native = new FakeNativeAquesTalk1();
        using var synth = Build(native);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => synth.SynthesizeAsync("f1", "こんにちは", new CancellationToken(canceled: true)));
        Assert.Equal(new[] { "create" }, native.Calls);   // convert/synth へ進まない
    }

    [Fact]
    public void Dispose_ReleasesKanjiHandle_AndDisposesNative()
    {
        var native = new FakeNativeAquesTalk1();
        var synth = Build(native);

        synth.Dispose();

        Assert.True(native.Released);
        Assert.True(native.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent_ReleasesOnce()
    {
        // 二重 Dispose で Release/create が増えない（_disposed ガード）。
        var native = new FakeNativeAquesTalk1();
        var synth = Build(native);

        synth.Dispose();
        synth.Dispose();

        Assert.Equal(1, native.Calls.Count(c => c == "release"));
        Assert.Equal(1, native.Calls.Count(c => c == "create"));
    }

    [Fact]
    public async Task Synthesize_SuccessCodeButEmptyBuffer_ThrowsSynthesisFailed()
    {
        // synErr==0 でも空バッファ（ネイティブ異常）は SYNTHESIS-FAILED に倒す防御ガード（WAQT-COV-04）。
        var native = new FakeNativeAquesTalk1 { SynthErrCode = 0, SynthResult = Array.Empty<byte>() };
        using var synth = Build(native);

        var ex = await Assert.ThrowsAsync<TtsException>(() => synth.SynthesizeAsync("f1", "漢字"));
        Assert.Equal("E-TTS-AQT-SYNTHESIS-FAILED-001", ex.Code);
    }
}
