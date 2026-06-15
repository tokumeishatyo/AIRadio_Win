using System.Net.Http.Headers;

namespace AIRadio.Infrastructure;

/// <summary><see cref="System.Net.Http.HttpClient"/> ベースの <see cref="IHttpClient"/> 実装。</summary>
public sealed class HttpClientAdapter : IHttpClient
{
    private readonly HttpClient _client;

    public HttpClientAdapter(HttpClient? client = null) => _client = client ?? new HttpClient();

    public Task<byte[]> GetAsync(Uri url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, url, null, headers, ct);

    public Task<byte[]> PostAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, url, body, headers, ct);

    public Task<byte[]> PutAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, url, body, headers, ct);

    private async Task<byte[]> SendAsync(
        HttpMethod method, Uri url, byte[]? body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            if (headers is not null && headers.TryGetValue("Content-Type", out var contentType))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            request.Content = content;
        }

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Content ヘッダとして設定済み
                }
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpStatusException((int)response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
