using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class NewsWeatherProviderTests
{
    [Fact]
    public async Task Announcement_ComposesNewsAndWeatherIntoTemplate()
    {
        var provider = new NewsWeatherProvider(
            news: new StubResearchSource("ニュース本文。"),
            weather: new StubResearchSource("天気本文。"),
            template: "ニュース: {news} 天気: {weather}");

        var text = await provider.AnnouncementAsync();

        Assert.Equal("ニュース: ニュース本文。 天気: 天気本文。", text);
    }

    [Fact]
    public async Task Announcement_UsesFallbacks_WhenFetchFails()
    {
        var provider = new NewsWeatherProvider(
            news: new StubResearchSource(ResearchException.NewsFetchFailed("x")),
            weather: new StubResearchSource(ResearchException.WeatherFetchFailed("y")),
            template: "{news}|{weather}",
            newsFallback: "NEWS_NG",
            weatherFallback: "WX_NG");

        var text = await provider.AnnouncementAsync();

        Assert.Equal("NEWS_NG|WX_NG", text); // fail-tolerant
    }

    [Fact]
    public async Task Announcement_PropagatesCancellation_NotFallback()
    {
        // 停止（キャンセル）はフォールバックに握り潰さず伝播させる（完全静寂 §3-1）。
        var provider = new NewsWeatherProvider(
            news: new StubResearchSource(new OperationCanceledException()),
            weather: new StubResearchSource("天気。"),
            template: "{news}|{weather}",
            newsFallback: "NEWS_NG");

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.AnnouncementAsync());
    }

    /// <summary>固定の成功文字列または例外を返す IResearchSource スタブ。</summary>
    private sealed class StubResearchSource : IResearchSource
    {
        private readonly string? _result;
        private readonly Exception? _error;

        public StubResearchSource(string result) => _result = result;
        public StubResearchSource(Exception error) => _error = error;

        public Task<string> FetchAsync(CancellationToken ct = default)
            => _error is not null ? Task.FromException<string>(_error) : Task.FromResult(_result!);
    }
}
