namespace AIRadio.Infrastructure;

/// <summary>
/// HTTP 通信の抽象（VOICEVOX / Spotify / News / 天気 が共通利用）。
/// Infrastructure 内の実装を単体テスト可能にするための seam（Core は HTTP を知らない）。
/// </summary>
public interface IHttpClient
{
    Task<byte[]> GetAsync(Uri url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);
    Task<byte[]> PostAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);
    Task<byte[]> PutAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);
}

/// <summary>HTTP のステータス異常（非 2xx）。呼び出し側がステータスでパターンマッチできるよう数値を保持。</summary>
public sealed class HttpStatusException : Exception
{
    public int StatusCode { get; }

    public HttpStatusException(int statusCode) : base($"HTTP status {statusCode}") => StatusCode = statusCode;
}
