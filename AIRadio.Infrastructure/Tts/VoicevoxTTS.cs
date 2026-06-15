using System.Text;
using System.Text.Json.Nodes;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// VOICEVOX のローカル HTTP API で日本語テキストを WAV 合成する <see cref="ITTSBackend"/> 実装。
/// audio_query（解析）→ synthesis（合成）の 2 段呼び出し。
/// </summary>
public sealed class VoicevoxTTS : ITTSBackend
{
    private readonly Uri _base;
    private readonly IHttpClient _http;
    /// <summary>話速（VOICEVOX の speedScale、1.0 = 標準）。config/tts.yaml の speed_scale。</summary>
    private readonly double _speedScale;

    public VoicevoxTTS(string endpoint, IHttpClient http, double speedScale = 1.0)
    {
        _base = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : new Uri("http://127.0.0.1:50021/");
        _http = http;
        _speedScale = speedScale;
    }

    public async Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
    {
        try
        {
            var queryUrl = MakeUrl("audio_query", new Dictionary<string, string>
            {
                ["text"] = NormalizeForSpeech(text),
                ["speaker"] = speakerId.ToString(),
            });
            var query = await _http.PostAsync(queryUrl, body: null, headers: null, ct).ConfigureAwait(false);

            var synthUrl = MakeUrl("synthesis", new Dictionary<string, string>
            {
                ["speaker"] = speakerId.ToString(),
            });
            var wav = await _http.PostAsync(
                synthUrl,
                body: ApplySpeed(query),
                headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                ct).ConfigureAwait(false);
            return wav;
        }
        catch (TtsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpStatusException ex)
        {
            throw TtsException.SynthesisFailed($"HTTP {ex.StatusCode}");
        }
        catch (HttpRequestException)
        {
            throw TtsException.Unreachable();
        }
        catch (Exception ex)
        {
            throw TtsException.SynthesisFailed(ex.Message);
        }
    }

    /// <summary>
    /// 発話前のテキスト正規化。VOICEVOX は波ダッシュ「〜」(U+301C) / 全角チルダ「～」(U+FF5E) を
    /// 伸ばす音として読まず区切るため、長音「ー」(U+30FC) に置換する。
    /// </summary>
    public static string NormalizeForSpeech(string text)
        => text.Replace('〜', 'ー').Replace('～', 'ー');

    /// <summary>audio_query の結果 JSON に話速（speedScale）を適用する。標準速（1.0）なら無加工。</summary>
    private byte[] ApplySpeed(byte[] query)
    {
        if (_speedScale == 1.0)
        {
            return query;
        }

        try
        {
            var node = JsonNode.Parse(Encoding.UTF8.GetString(query));
            if (node is null)
            {
                throw TtsException.SynthesisFailed("audio_query の応答を解釈できません");
            }
            node["speedScale"] = _speedScale;
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        catch (TtsException)
        {
            throw;
        }
        catch (Exception)
        {
            throw TtsException.SynthesisFailed("audio_query の応答を解釈できません");
        }
    }

    private Uri MakeUrl(string path, IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder(new Uri(_base, path));
        var sb = new StringBuilder();
        foreach (var (key, value) in query)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        builder.Query = sb.ToString();
        return builder.Uri;
    }
}
