using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W15 <see cref="SpotifyArtistCatalog"/>（artist 検索 → top-tracks の 2 段）。
/// 未解決は空（throw しない）、HTTP エラーは <c>E-SPT-SEARCH-FAILED-001</c>。
/// </summary>
public class SpotifyArtistCatalogTests
{
    private const string ArtistSearchJson = "{\"artists\":{\"items\":[{\"id\":\"ART1\"}]}}";

    private const string TopTracksJson =
        "{\"tracks\":[" +
        "{\"uri\":\"spotify:track:1\",\"name\":\"曲1\",\"artists\":[{\"name\":\"米津玄師\"}],\"is_playable\":true}," +
        "{\"uri\":\"spotify:track:2\",\"name\":\"曲2\",\"artists\":[{\"name\":\"米津玄師\"}],\"is_playable\":true}]}";

    [Fact]
    public async Task TopTracks_ResolvesArtist_ThenFetchesTopTracks_WithMarketAndBearer()
    {
        var http = new FakeHttpClient(url =>
            url.AbsoluteUri.Contains("/search")
                ? Encoding.UTF8.GetBytes(ArtistSearchJson)
                : Encoding.UTF8.GetBytes(TopTracksJson));
        var catalog = new SpotifyArtistCatalog(new FakeTokenProvider("TOK"), http, "JP");

        var tracks = await catalog.TopTracksAsync("米津玄師", 10);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("spotify:track:1", tracks[0].Uri);
        Assert.Equal("曲1", tracks[0].Title);
        Assert.Equal("米津玄師", tracks[0].Artist);
        // 1 回目 = artist 検索、2 回目 = top-tracks。いずれも market=JP / Bearer。
        Assert.Contains("type=artist", http.Requests[0].Url.Query);
        Assert.Contains("market=JP", http.Requests[0].Url.Query);
        Assert.Contains("/artists/ART1/top-tracks", http.Requests[1].Url.AbsoluteUri);
        Assert.Contains("market=JP", http.Requests[1].Url.Query);
        Assert.Equal("Bearer TOK", http.Requests[1].Headers!["Authorization"]);
    }

    [Fact]
    public async Task TopTracks_ArtistNotFound_ReturnsEmpty_NoSecondCall()
    {
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes("{\"artists\":{\"items\":[]}}"));
        var catalog = new SpotifyArtistCatalog(new FakeTokenProvider(), http, "JP");

        var tracks = await catalog.TopTracksAsync("無名", 10);

        Assert.Empty(tracks);
        Assert.Single(http.Requests);   // 未解決なら top-tracks は呼ばない（throw もしない）
    }

    [Fact]
    public async Task TopTracks_Limit_TakesAtMostLimit()
    {
        var http = new FakeHttpClient(url =>
            url.AbsoluteUri.Contains("/search")
                ? Encoding.UTF8.GetBytes(ArtistSearchJson)
                : Encoding.UTF8.GetBytes(TopTracksJson));
        var catalog = new SpotifyArtistCatalog(new FakeTokenProvider(), http, "JP");

        var tracks = await catalog.TopTracksAsync("米津玄師", 1);
        Assert.Single(tracks);   // limit=1
    }

    [Fact]
    public async Task TopTracks_OnHttpError_ThrowsSearchFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));
        var catalog = new SpotifyArtistCatalog(new FakeTokenProvider(), http, "JP");

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => catalog.TopTracksAsync("x", 10));
        Assert.Equal("E-SPT-SEARCH-FAILED-001", ex.Code);
    }
}
