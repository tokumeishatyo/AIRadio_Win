using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>Spotify アクセストークンの供給抽象（検索・再生が利用）。</summary>
public interface ISpotifyTokenProvider
{
    Task<string> ValidAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Authorization Code + PKCE による Spotify 認証。アクセストークンを期限付きでキャッシュし、
/// refresh トークンで無音更新する。初回は <see cref="AuthorizeAsync"/> でブラウザログイン。
/// </summary>
public sealed class SpotifyAuth : ISpotifyTokenProvider
{
    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly ushort _loopbackPort;
    private readonly IReadOnlyList<string> _scopes;
    private readonly ITokenStore _store;
    private readonly IHttpClient _http;
    private readonly IClock _clock;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public SpotifyAuth(
        string clientId, string redirectUri, ushort loopbackPort, IReadOnlyList<string> scopes,
        ITokenStore store, IHttpClient http, IClock clock)
    {
        _clientId = clientId;
        _redirectUri = redirectUri;
        _loopbackPort = loopbackPort;
        _scopes = scopes;
        _store = store;
        _http = http;
        _clock = clock;
    }

    public async Task<string> ValidAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _clock.Now < _expiry)
            {
                return _accessToken;
            }
            var refresh = _store.Load() ?? throw SpotifyException.AuthRequired();
            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = _clientId,
            }, ct).ConfigureAwait(false);
            Apply(token);
            if (token.RefreshToken is not null)
            {
                _store.Save(token.RefreshToken);
            }
            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>対話的 PKCE 認可。ブラウザを開きログイン → 認可コード受領 → トークン交換 → refresh 保管。</summary>
    public async Task AuthorizeAsync(CancellationToken ct = default)
    {
        var verifier = Pkce.GenerateVerifier();
        var challenge = Pkce.Challenge(verifier);

        var authorizeUrl = BuildAuthorizeUrl(challenge);
        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        var code = await new LoopbackServer().WaitForCodeAsync(_loopbackPort, ct).ConfigureAwait(false);

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Apply(token);
        }
        finally
        {
            _gate.Release();
        }
        if (token.RefreshToken is not null)
        {
            _store.Save(token.RefreshToken);
        }
    }

    private void Apply(TokenResponse token)
    {
        _accessToken = token.AccessToken;
        _expiry = _clock.Now.AddSeconds(token.ExpiresIn - 30);
    }

    private string BuildAuthorizeUrl(string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _redirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["scope"] = string.Join(' ', _scopes),
        };
        var sb = new StringBuilder("https://accounts.spotify.com/authorize?");
        var first = true;
        foreach (var (key, value) in query)
        {
            if (!first)
            {
                sb.Append('&');
            }
            first = false;
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    private async Task<TokenResponse> RequestTokenAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        try
        {
            var data = await _http.PostAsync(
                new Uri("https://accounts.spotify.com/api/token"),
                body: FormEncode(parameters),
                headers: new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
                ct).ConfigureAwait(false);

            var token = JsonSerializer.Deserialize<TokenResponse>(data);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
            {
                throw SpotifyException.AuthFailed("トークン応答を解釈できません");
            }
            return token;
        }
        catch (SpotifyException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SpotifyException.AuthFailed(ex.Message);
        }
    }

    /// <summary>application/x-www-form-urlencoded のボディを作る（RFC 3986 unreserved 以外をエスケープ）。</summary>
    public static byte[] FormEncode(IReadOnlyDictionary<string, string> parameters)
    {
        var pairs = parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        return Encoding.UTF8.GetBytes(string.Join('&', pairs));
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }
}
