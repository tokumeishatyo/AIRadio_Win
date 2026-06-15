using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;
using AIRadio.TestSupport;

namespace AIRadio.Infrastructure.Tests;

public class SpotifyAuthTests
{
    private static SpotifyAuth MakeAuth(FakeTokenStore store, FakeHttpClient http) =>
        new(
            clientId: "CID",
            redirectUri: "http://127.0.0.1:5543/callback",
            loopbackPort: 5543,
            scopes: new[] { "user-read-playback-state" },
            store: store,
            http: http,
            clock: new FakeClock());

    [Fact]
    public async Task RefreshesAccessToken_WhenRefreshTokenPresent()
    {
        var store = new FakeTokenStore("REFRESH");
        var http = new FakeHttpClient(_ =>
            Encoding.UTF8.GetBytes("{\"access_token\":\"NEW\",\"expires_in\":3600,\"refresh_token\":\"REFRESH2\"}"));
        var auth = MakeAuth(store, http);

        var token = await auth.ValidAccessTokenAsync();

        Assert.Equal("NEW", token);
        Assert.Equal("REFRESH2", store.Load()); // ローテートされた refresh が保存される
        Assert.Contains(http.Requests, r => r.Url.AbsoluteUri.Contains("accounts.spotify.com/api/token"));
    }

    [Fact]
    public async Task CachesAccessToken_AcrossCalls()
    {
        var store = new FakeTokenStore("REFRESH");
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes("{\"access_token\":\"NEW\",\"expires_in\":3600}"));
        var auth = MakeAuth(store, http);

        await auth.ValidAccessTokenAsync();
        await auth.ValidAccessTokenAsync();

        var tokenCalls = http.Requests.Count(r => r.Url.AbsoluteUri.Contains("api/token"));
        Assert.Equal(1, tokenCalls); // 2 回呼んでもトークン取得は 1 回（キャッシュ）
    }

    [Fact]
    public async Task ThrowsAuthRequired_WhenNoRefreshToken()
    {
        var auth = MakeAuth(new FakeTokenStore(), new FakeHttpClient(_ => Array.Empty<byte>()));

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => auth.ValidAccessTokenAsync());
        Assert.Equal("E-SPT-AUTH-REQUIRED-001", ex.Code);
    }

    [Fact]
    public void FormEncode_EscapesValues()
    {
        var body = Encoding.UTF8.GetString(SpotifyAuth.FormEncode(
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["x"] = "a b/c" }));

        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("x=a%20b%2Fc", body);
    }
}
