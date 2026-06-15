using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class VoicevoxTTSTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Synthesize_CallsAudioQueryThenSynthesis_AndReturnsWav()
    {
        var http = new FakeHttpClient(url =>
            url.AbsolutePath.EndsWith("audio_query") ? Bytes("{\"speedScale\":1.0}")
            : url.AbsolutePath.EndsWith("synthesis") ? Bytes("WAVDATA")
            : throw new InvalidOperationException(url.ToString()));

        var tts = new VoicevoxTTS("http://127.0.0.1:50021/", http, speedScale: 1.0);
        var wav = await tts.SynthesizeAsync("こんにちは", 3);

        Assert.Equal("WAVDATA", Encoding.UTF8.GetString(wav));
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("audio_query", http.Requests[0].Url.AbsoluteUri);
        Assert.Contains("speaker=3", http.Requests[0].Url.Query);
        Assert.Contains("synthesis", http.Requests[1].Url.AbsoluteUri);
        Assert.Equal("application/json", http.Requests[1].Headers!["Content-Type"]);
    }

    [Fact]
    public async Task Synthesize_AppliesSpeedScaleToQueryBody()
    {
        var http = new FakeHttpClient(url =>
            url.AbsolutePath.EndsWith("audio_query") ? Bytes("{\"speedScale\":1.0,\"x\":1}") : Bytes("WAV"));

        var tts = new VoicevoxTTS("http://127.0.0.1:50021/", http, speedScale: 1.5);
        await tts.SynthesizeAsync("test", 3);

        var body = Encoding.UTF8.GetString(http.Requests[1].Body!);
        Assert.Contains("\"speedScale\":1.5", body);
    }

    [Fact]
    public void NormalizeForSpeech_ReplacesWaveDashAndTildeWithLongVowel()
    {
        Assert.Equal("あーし", VoicevoxTTS.NormalizeForSpeech("あ〜し"));
        Assert.Equal("あーし", VoicevoxTTS.NormalizeForSpeech("あ～し"));
    }

    [Fact]
    public async Task Synthesize_ConnectionFailure_ThrowsUnreachable()
    {
        var http = new FakeHttpClient(_ => throw new HttpRequestException("connection refused"));
        var tts = new VoicevoxTTS("http://127.0.0.1:50021/", http);

        var ex = await Assert.ThrowsAsync<TtsException>(() => tts.SynthesizeAsync("x", 3));
        Assert.Equal("E-TTS-UNREACHABLE-001", ex.Code);
    }

    [Fact]
    public async Task Synthesize_HttpStatusError_ThrowsSynthesisFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));
        var tts = new VoicevoxTTS("http://127.0.0.1:50021/", http);

        var ex = await Assert.ThrowsAsync<TtsException>(() => tts.SynthesizeAsync("x", 3));
        Assert.Equal("E-TTS-SYNTHESIS-FAILED-001", ex.Code);
    }
}
