using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>テスト用 HTTP fake。リクエストを記録し、URL に応じた応答を返す/投げる。</summary>
internal sealed class FakeHttpClient : IHttpClient
{
    public sealed record Request(string Method, Uri Url, byte[]? Body, IReadOnlyDictionary<string, string>? Headers);

    private readonly object _lock = new();
    private readonly List<Request> _requests = new();
    private readonly Func<Uri, byte[]> _responder;

    public FakeHttpClient(Func<Uri, byte[]> responder) => _responder = responder;

    public IReadOnlyList<Request> Requests
    {
        get { lock (_lock) { return _requests.ToList(); } }
    }

    public Task<byte[]> GetAsync(Uri url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => Record("GET", url, null, headers);

    public Task<byte[]> PostAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => Record("POST", url, body, headers);

    public Task<byte[]> PutAsync(Uri url, byte[]? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => Record("PUT", url, body, headers);

    private Task<byte[]> Record(string method, Uri url, byte[]? body, IReadOnlyDictionary<string, string>? headers)
    {
        lock (_lock) { _requests.Add(new Request(method, url, body, headers)); }
        return Task.FromResult(_responder(url));
    }
}
