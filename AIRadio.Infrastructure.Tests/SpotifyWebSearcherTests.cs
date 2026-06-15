using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class SpotifyWebSearcherTests
{
    [Fact]
    public async Task Search_ParsesTracks_AndSendsBearerAndMarket()
    {
        const string json =
            "{\"tracks\":{\"items\":[{\"uri\":\"spotify:track:1\",\"name\":\"Idol\"," +
            "\"artists\":[{\"name\":\"YOASOBI\"}],\"is_playable\":true}]}}";
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes(json));
        var searcher = new SpotifyWebSearcher(new FakeTokenProvider("TOK"), http, "JP");

        var results = await searcher.SearchAsync("YOASOBI", 5);

        Assert.Single(results);
        Assert.Equal("spotify:track:1", results[0].Uri);
        Assert.Equal("Idol", results[0].Title);
        Assert.Equal("YOASOBI", results[0].Artist);
        Assert.Equal("Bearer TOK", http.Requests[0].Headers!["Authorization"]);
        Assert.Contains("market=JP", http.Requests[0].Url.Query);
    }

    [Fact]
    public async Task IsPlayable_ReadsTrackObject_AndHitsTrackEndpoint()
    {
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes("{\"is_playable\":false}"));
        var searcher = new SpotifyWebSearcher(new FakeTokenProvider(), http, "JP");

        Assert.False(await searcher.IsPlayableAsync("spotify:track:abc"));
        Assert.Contains("tracks/abc", http.Requests[0].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Search_OnHttpError_ThrowsSearchFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));
        var searcher = new SpotifyWebSearcher(new FakeTokenProvider(), http, "JP");

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => searcher.SearchAsync("x", 5));
        Assert.Equal("E-SPT-SEARCH-FAILED-001", ex.Code);
    }
}
