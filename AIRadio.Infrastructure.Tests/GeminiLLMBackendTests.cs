using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class GeminiLLMBackendTests
{
    private static byte[] CandidatesJson(string text) => Encoding.UTF8.GetBytes(
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" +
        System.Text.Json.JsonSerializer.Serialize(text) + "}]}}]}");

    [Fact]
    public async Task Generate_ParsesText_AndSendsApiKeyHeader_NotInUrl()
    {
        var http = new FakeHttpClient(_ => CandidatesJson("台本本文"));
        var llm = new GeminiLLMBackend("https://generativelanguage.googleapis.com/", "gemini-3.1-flash-lite", "SECRET", http);

        var text = await llm.GenerateAsync(new LLMRequest("プロンプト", System: "システム", Temperature: 0.9));

        Assert.Equal("台本本文", text);
        var req = http.Requests[0];
        Assert.Equal("POST", req.Method);
        Assert.Contains("v1beta/models/gemini-3.1-flash-lite:generateContent", req.Url.AbsoluteUri);
        Assert.Equal("SECRET", req.Headers!["x-goog-api-key"]); // キーはヘッダで送る
        Assert.DoesNotContain("SECRET", req.Url.AbsoluteUri);    // URL には出さない
        var body = Encoding.UTF8.GetString(req.Body!);
        Assert.Contains("プロンプト", body);
        Assert.Contains("システム", body);                       // systemInstruction
    }

    [Fact]
    public async Task Generate_OnHttpError_ThrowsApiFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(429));
        var llm = new GeminiLLMBackend("https://x/", "m", "K", http);

        var ex = await Assert.ThrowsAsync<LlmException>(() => llm.GenerateAsync(new LLMRequest("p")));
        Assert.Equal("E-LLM-API-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Generate_OnEmptyCandidates_ThrowsEmptyResponse()
    {
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes("{\"candidates\":[]}"));
        var llm = new GeminiLLMBackend("https://x/", "m", "K", http);

        var ex = await Assert.ThrowsAsync<LlmException>(() => llm.GenerateAsync(new LLMRequest("p")));
        Assert.Equal("E-LLM-EMPTY-RESPONSE-001", ex.Code);
    }

    [Fact]
    public async Task Generate_PropagatesCancellation_NotApiFailed()
    {
        // 停止（キャンセル）は E-LLM-API-FAILED-001 に包まず伝播させる（完全静寂 §3-1）。
        var http = new FakeHttpClient(_ => throw new OperationCanceledException());
        var llm = new GeminiLLMBackend("https://x/", "m", "K", http);

        await Assert.ThrowsAsync<OperationCanceledException>(() => llm.GenerateAsync(new LLMRequest("p")));
    }

    [Fact]
    public async Task Generate_NoSystem_OmitsSystemInstruction()
    {
        var http = new FakeHttpClient(_ => CandidatesJson("ok"));
        var llm = new GeminiLLMBackend("https://x/", "m", "K", http);

        await llm.GenerateAsync(new LLMRequest("プロンプトのみ"));

        var body = Encoding.UTF8.GetString(http.Requests[0].Body!);
        Assert.DoesNotContain("systemInstruction", body); // System=null のとき省略される
    }
}
