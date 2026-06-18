using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// Spotify Web API でアーティストの代表曲（top-tracks）を取得する <see cref="IArtistCatalog"/> 実装（仕様 w15 §5）。
/// 1) <c>GET /v1/search?type=artist&amp;limit=1</c> で名前からアーティスト ID を解決（先頭ヒット）。
/// 2) <c>GET /v1/artists/{id}/top-tracks?market=...</c> で上位曲を取得し <see cref="TrackInfo"/> へ変換。
/// market は <c>spotify.local.yaml</c> の値（検索・top-tracks 共通、既定 JP）。アーティスト未解決は空リスト（throw しない）。
/// 重複除外・最大 7 曲・K 判定は呼び出し側（<see cref="ArtistFeatureEngine"/>）が行う＝本クラスは thin に保つ。
/// </summary>
public sealed class SpotifyArtistCatalog : IArtistCatalog
{
    private readonly ISpotifyTokenProvider _auth;
    private readonly IHttpClient _http;
    private readonly string _market;

    public SpotifyArtistCatalog(ISpotifyTokenProvider auth, IHttpClient http, string market = "JP")
    {
        _auth = auth;
        _http = http;
        _market = market;
    }

    public async Task<IReadOnlyList<TrackInfo>> TopTracksAsync(string artistName, int limit, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        try
        {
            var artistId = await ResolveArtistIdAsync(artistName, token, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(artistId))
            {
                return Array.Empty<TrackInfo>();   // 未解決（非実在・配信なし等）。K<3 スキップへ流す。
            }
            var url = new Uri(
                $"https://api.spotify.com/v1/artists/{Uri.EscapeDataString(artistId)}/top-tracks?market={_market}");
            var data = await _http.GetAsync(url, Bearer(token), ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<TopTracksResponse>(data);
            var tracks = response?.Tracks ?? new List<TrackObject>();
            return tracks
                .Take(limit)
                .Select(t => new TrackInfo(
                    t.Uri ?? "", t.Name ?? "", t.Artists?.FirstOrDefault()?.Name ?? artistName, t.IsPlayable ?? true))
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

    private async Task<string?> ResolveArtistIdAsync(string name, string token, CancellationToken ct)
    {
        var url = new Uri(
            $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(name)}&type=artist&limit=1&market={_market}");
        var data = await _http.GetAsync(url, Bearer(token), ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ArtistSearchResponse>(data);
        return response?.Artists?.Items?.FirstOrDefault()?.Id;
    }

    private static Dictionary<string, string> Bearer(string token) =>
        new() { ["Authorization"] = $"Bearer {token}" };

    private sealed class ArtistSearchResponse
    {
        [JsonPropertyName("artists")] public ArtistsObject? Artists { get; set; }
    }

    private sealed class ArtistsObject
    {
        [JsonPropertyName("items")] public List<ArtistItem>? Items { get; set; }
    }

    private sealed class ArtistItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class TopTracksResponse
    {
        [JsonPropertyName("tracks")] public List<TrackObject>? Tracks { get; set; }
    }

    private sealed class TrackObject
    {
        [JsonPropertyName("uri")] public string? Uri { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("artists")] public List<ArtistRef>? Artists { get; set; }
        [JsonPropertyName("is_playable")] public bool? IsPlayable { get; set; }
    }

    private sealed class ArtistRef
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
