using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// Spotify Web API で曲検索・再生可否確認を行う <see cref="ITrackSearcher"/> 実装。
/// アクセストークンは <see cref="ISpotifyTokenProvider"/>（PKCE 認証）から取得する。
/// </summary>
public sealed class SpotifyWebSearcher : ITrackSearcher
{
    private readonly ISpotifyTokenProvider _auth;
    private readonly IHttpClient _http;
    private readonly string _market;

    public SpotifyWebSearcher(ISpotifyTokenProvider auth, IHttpClient http, string market = "JP")
    {
        _auth = auth;
        _http = http;
        _market = market;
    }

    public async Task<IReadOnlyList<TrackInfo>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        try
        {
            var url = new Uri(
                $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit={limit}&market={_market}");
            var data = await _http.GetAsync(url, Bearer(token), ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SearchResponse>(data);
            var items = response?.Tracks?.Items ?? new List<TrackObject>();
            return items
                .Select(i => new TrackInfo(
                    i.Uri ?? "", i.Name ?? "", i.Artists?.FirstOrDefault()?.Name ?? "", i.IsPlayable ?? true))
                .ToList();
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
            throw SpotifyException.SearchFailed(ex.Message);
        }
    }

    public async Task<bool> IsPlayableAsync(string uri, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        try
        {
            var id = SpotifyUri.TrackId(uri);
            var url = new Uri($"https://api.spotify.com/v1/tracks/{id}?market={_market}");
            var data = await _http.GetAsync(url, Bearer(token), ct).ConfigureAwait(false);
            var track = JsonSerializer.Deserialize<TrackObject>(data);
            return track?.IsPlayable ?? true;
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
            throw SpotifyException.SearchFailed(ex.Message);
        }
    }

    private static Dictionary<string, string> Bearer(string token) =>
        new() { ["Authorization"] = $"Bearer {token}" };

    private sealed class SearchResponse
    {
        [JsonPropertyName("tracks")] public Tracks? Tracks { get; set; }
    }

    private sealed class Tracks
    {
        [JsonPropertyName("items")] public List<TrackObject>? Items { get; set; }
    }

    private sealed class TrackObject
    {
        [JsonPropertyName("uri")] public string? Uri { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("artists")] public List<Artist>? Artists { get; set; }
        [JsonPropertyName("is_playable")] public bool? IsPlayable { get; set; }
    }

    private sealed class Artist
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
