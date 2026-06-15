using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>固定アクセストークンを返すテスト用プロバイダ。</summary>
internal sealed class FakeTokenProvider : ISpotifyTokenProvider
{
    private readonly string _token;

    public FakeTokenProvider(string token = "TOK") => _token = token;

    public Task<string> ValidAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
}

/// <summary>インメモリの TokenStore（テスト用）。</summary>
internal sealed class FakeTokenStore : ITokenStore
{
    private readonly object _lock = new();
    private string? _value;

    public FakeTokenStore(string? initial = null) => _value = initial;

    public void Save(string value)
    {
        lock (_lock) { _value = value; }
    }

    public string? Load()
    {
        lock (_lock) { return _value; }
    }

    public void Delete()
    {
        lock (_lock) { _value = null; }
    }
}
