using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class NewsRssSourceTests
{
    private static readonly byte[] Rss = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<rss version=\"2.0\"><channel>" +
        "<title>Google ニュース</title>" +
        "<item><title>関東甲信が梅雨入り - tenki.jp</title><link>https://x</link></item>" +
        "<item><title>京都で行方不明のニュース - FNNプライムオンライン</title></item>" +
        "<item><title>速報 - 自転車 - 読売新聞</title></item>" +
        "</channel></rss>");

    [Fact]
    public async Task Fetch_ParsesItemTitles_StripsSource_ExcludesChannelTitle()
    {
        var http = new FakeHttpClient(_ => Rss);
        var source = new NewsRssSource("https://news.google.com/rss", http, maxItems: 5);

        var text = await source.FetchAsync();

        Assert.Contains("関東甲信が梅雨入り", text);
        Assert.Contains("京都で行方不明のニュース", text);
        Assert.DoesNotContain("tenki.jp", text);        // メディア名は除去
        Assert.DoesNotContain("Google ニュース", text);  // チャンネルタイトルは除外
        Assert.Contains("速報 - 自転車", text);          // 見出し内の " - " は保持（末尾のみ除去）
    }

    [Fact]
    public async Task Fetch_LimitsToMaxItems()
    {
        var http = new FakeHttpClient(_ => Rss);
        var source = new NewsRssSource("https://x", http, maxItems: 1);

        var text = await source.FetchAsync();

        Assert.Contains("関東甲信が梅雨入り", text);
        Assert.DoesNotContain("京都で行方不明", text);
    }

    [Theory]
    [InlineData("見出し - メディア", "見出し")]
    [InlineData("A - B - メディア", "A - B")]
    [InlineData("単独見出し", "単独見出し")]
    public void CleanTitle_StripsTrailingSource(string raw, string expected)
        => Assert.Equal(expected, NewsRssSource.CleanTitle(raw));

    [Fact]
    public async Task Fetch_OnHttpError_ThrowsNewsFetchFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));
        var source = new NewsRssSource("https://x", http, maxItems: 5);

        var ex = await Assert.ThrowsAsync<ResearchException>(() => source.FetchAsync());
        Assert.Equal("E-NEWS-FETCH-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Fetch_OnEmptyFeed_ThrowsNewsFetchFailed()
    {
        var http = new FakeHttpClient(_ => Encoding.UTF8.GetBytes(
            "<rss version=\"2.0\"><channel><title>空</title></channel></rss>"));
        var source = new NewsRssSource("https://x", http, maxItems: 5);

        var ex = await Assert.ThrowsAsync<ResearchException>(() => source.FetchAsync());
        Assert.Equal("E-NEWS-FETCH-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Fetch_ExcludesNamespacedTitles_AndParsesCdata()
    {
        // Google News RSS は media:/dc: 等の名前空間を宣言する。plain <title>（CDATA 可）のみ採用する。
        var rss = Encoding.UTF8.GetBytes(
            "<rss xmlns:media=\"http://m\" xmlns:dc=\"http://d\" version=\"2.0\"><channel>" +
            "<title>チャンネル</title>" +
            "<item>" +
            "<media:title>名前空間タイトル</media:title>" +
            "<title><![CDATA[本物の見出し - メディア]]></title>" +
            "<dc:title>別名前空間</dc:title>" +
            "</item></channel></rss>");
        var http = new FakeHttpClient(_ => rss);
        var source = new NewsRssSource("https://x", http, maxItems: 5);

        var text = await source.FetchAsync();

        Assert.Contains("本物の見出し", text);        // CDATA の plain title を採用
        Assert.DoesNotContain("メディア", text);       // 末尾メディア名は除去
        Assert.DoesNotContain("名前空間タイトル", text); // media:title は除外
        Assert.DoesNotContain("別名前空間", text);      // dc:title は除外
        Assert.DoesNotContain("チャンネル", text);      // channel title は除外
    }

    [Fact]
    public async Task Fetch_PropagatesCancellation()
    {
        // 停止（キャンセル）は ResearchException に包まず伝播させる（完全静寂 §3-1）。
        var http = new FakeHttpClient(_ => throw new OperationCanceledException());
        var source = new NewsRssSource("https://x", http, maxItems: 5);

        await Assert.ThrowsAsync<OperationCanceledException>(() => source.FetchAsync());
    }
}
