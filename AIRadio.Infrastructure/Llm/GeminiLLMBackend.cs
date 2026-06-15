using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// Gemini API（generateContent）で台本テキストを生成する <see cref="ILLMBackend"/> 実装。Mac 版 `GeminiLLMBackend` の移植。
/// API キーは URL ではなく <c>x-goog-api-key</c> ヘッダで送る（ログ・エラーに漏れない）。
/// </summary>
public sealed class GeminiLLMBackend : ILLMBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 日本語を \uXXXX にエスケープせず UTF-8 リテラルで送る（Mac の JSONEncoder と同じワイヤ形式）。
        // API ボディ用途のため Relaxed エンコーダで問題ない（HTML 埋め込みではない）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly IHttpClient _http;

    public GeminiLLMBackend(string endpoint, string model, string apiKey, IHttpClient http)
    {
        _endpoint = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
        _model = model;
        _apiKey = apiKey;
        _http = http;
    }

    public GeminiLLMBackend(LlmConfig config, IHttpClient http)
        : this(config.Endpoint, config.Model, config.ApiKey, http)
    {
    }

    public async Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
    {
        var url = new Uri($"{_endpoint}v1beta/models/{_model}:generateContent");
        var body = JsonSerializer.SerializeToUtf8Bytes(BuildRequest(request), JsonOptions);

        byte[] data;
        try
        {
            data = await _http.PostAsync(
                url,
                body,
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["x-goog-api-key"] = _apiKey,
                },
                ct).ConfigureAwait(false);
        }
        catch (HttpStatusException ex)
        {
            throw LlmException.ApiFailed($"HTTP {ex.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 接続失敗など（Mac の `catch is URLError` 相当）。内部例外文を漏らさず固定文言にする。
            throw LlmException.ApiFailed("Gemini API に接続できません");
        }

        GeminiResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<GeminiResponse>(data);
        }
        catch
        {
            throw LlmException.ApiFailed("応答 JSON を解釈できません");
        }

        var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts ?? new List<PartResp>();
        var text = string.Concat(parts.Select(p => p.Text ?? ""));
        if (text.Length == 0)
        {
            throw LlmException.EmptyResponse();
        }
        return text;
    }

    private static GeminiRequest BuildRequest(LLMRequest request) => new(
        Contents: new[] { new Content("user", new[] { new Part(request.Prompt) }) },
        SystemInstruction: request.System is null ? null : new Content(null, new[] { new Part(request.System) }),
        GenerationConfig: new GenConfig(request.Temperature));

    // MARK: - API の JSON 形

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] IReadOnlyList<Content> Contents,
        [property: JsonPropertyName("systemInstruction")] Content? SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GenConfig GenerationConfig);

    private sealed record Content(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<Part> Parts);

    private sealed record Part([property: JsonPropertyName("text")] string? Text);

    private sealed record GenConfig([property: JsonPropertyName("temperature")] double Temperature);

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")] public List<Candidate>? Candidates { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("content")] public ContentResp? Content { get; set; }
    }

    private sealed class ContentResp
    {
        [JsonPropertyName("parts")] public List<PartResp>? Parts { get; set; }
    }

    private sealed class PartResp
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
