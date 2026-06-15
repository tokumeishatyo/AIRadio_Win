using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

public class FakesTests
{
    [Fact]
    public async Task EchoLLM_EchoesPrompt()
    {
        var result = await new EchoLLM().GenerateAsync(new LLMRequest("hello"));
        Assert.Equal("echo: hello", result);
    }

    [Fact]
    public async Task InMemoryTTS_ReturnsNonEmptyData()
    {
        var data = await new InMemoryTTS().SynthesizeAsync("こんにちは", 3);
        Assert.NotEmpty(data);
    }

    [Fact]
    public async Task FakeClock_DelayIsImmediate_AndNowIsFixed()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        await clock.DelayAsync(123); // 即時（待たない）
        Assert.Equal(DateTimeOffset.UnixEpoch, clock.Now);
    }

    [Fact]
    public async Task FakeSpotifyController_RecordsEventOrder()
    {
        var spotify = new FakeSpotifyController();
        await spotify.PlayAsync("spotify:track:1");
        await spotify.SetVolumeAsync(50);
        await spotify.PauseAsync();

        Assert.Equal(
            new SpotifyEvent[]
            {
                new SpotifyEvent.Play("spotify:track:1"),
                new SpotifyEvent.SetVolume(50),
                new SpotifyEvent.Pause(),
            },
            spotify.Events);
    }

    [Fact]
    public async Task FakeTrackSearcher_RecordsQuery_AndReportsPlayability()
    {
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:1", "Song", "Artist"),
            new TrackInfo("spotify:track:2", "Other", "Artist", IsPlayable: false),
        });

        var results = await searcher.SearchAsync("Song", 10);

        Assert.Equal(2, results.Count);
        Assert.Equal(new[] { "Song" }, searcher.Queries);
        Assert.True(await searcher.IsPlayableAsync("spotify:track:1"));
        Assert.False(await searcher.IsPlayableAsync("spotify:track:2"));
        Assert.False(await searcher.IsPlayableAsync("spotify:track:unknown"));
    }

    [Fact]
    public async Task SpyAudioPlayer_RecordsPlayedData()
    {
        var player = new SpyAudioPlayer();
        await player.PlayAsync(new byte[] { 1, 2, 3 });
        Assert.Single(player.Played);
    }

    [Fact]
    public async Task FakeResearchSource_ReturnsPayload()
    {
        Assert.Equal("news", await new FakeResearchSource("news").FetchAsync());
    }

    [Fact]
    public void CollectingRadioLog_RecordsMessages()
    {
        var log = new CollectingRadioLog();
        log.Log("a");
        log.Log("b");
        Assert.Equal(new[] { "a", "b" }, log.Messages);
    }
}
