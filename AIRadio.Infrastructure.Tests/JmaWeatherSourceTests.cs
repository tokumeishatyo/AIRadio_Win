using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class JmaWeatherSourceTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes(
        "[{\"publishingOffice\":\"気象庁\",\"reportDatetime\":\"2026-06-07T11:00:00+09:00\"," +
        "\"timeSeries\":[{\"timeDefines\":[\"2026-06-07T11:00:00+09:00\"],\"areas\":[" +
        "{\"area\":{\"name\":\"東京地方\",\"code\":\"130010\"},\"weathers\":[\"くもり　夕方　から　雨\",\"雨\",\"くもり\"]}," +
        "{\"area\":{\"name\":\"伊豆諸島北部\",\"code\":\"130020\"},\"weathers\":[\"晴れ\"]}]}]}]");

    [Fact]
    public async Task Fetch_ExtractsTodayWeatherForArea_AndNormalizes()
    {
        var http = new FakeHttpClient(_ => Json);
        var source = new JmaWeatherSource("130000", "東京地方", http);

        var text = await source.FetchAsync();

        Assert.Contains("東京地方", text);
        Assert.Contains("くもり夕方から雨", text); // 全角空白が除去されている
        Assert.DoesNotContain("　", text);
    }

    [Fact]
    public void Normalize_RemovesIdeographicSpace()
        => Assert.Equal("くもりのち晴れ", JmaWeatherSource.Normalize("くもり　のち　晴れ"));

    [Fact]
    public async Task Fetch_OnHttpError_ThrowsWeatherFetchFailed()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(404));
        var source = new JmaWeatherSource("999999", "x", http);

        var ex = await Assert.ThrowsAsync<ResearchException>(() => source.FetchAsync());
        Assert.Equal("E-WX-FETCH-FAILED-001", ex.Code);
    }

    [Fact]
    public async Task Fetch_FallsBackToFirstAreaWithWeathers_WhenNamedAreaAbsent()
    {
        // 該当 areaName が無い場合は weathers を持つ先頭エリアに倒す（地域名は設定値のまま）。
        var json = Encoding.UTF8.GetBytes(
            "[{\"timeSeries\":[{\"areas\":[{\"area\":{\"name\":\"他県\"},\"weathers\":[\"晴れ\"]}]}]}]");
        var http = new FakeHttpClient(_ => json);
        var source = new JmaWeatherSource("130000", "東京地方", http); // 東京地方は JSON に無い

        var text = await source.FetchAsync();

        Assert.Contains("晴れ", text);      // 先頭の weathers 保持エリアにフォールバック
        Assert.Contains("東京地方", text);  // 出力の地域名は設定の areaName のまま
    }

    [Fact]
    public async Task Fetch_PropagatesCancellation()
    {
        var http = new FakeHttpClient(_ => throw new OperationCanceledException());
        var source = new JmaWeatherSource("130000", "東京地方", http);

        await Assert.ThrowsAsync<OperationCanceledException>(() => source.FetchAsync());
    }

    [Fact]
    public async Task Fetch_WhenAreaNotFound_ThrowsWeatherFetchFailed()
    {
        // 対象 areaName が無く、weathers を持つエリアも無い → 取得不可。
        var json = Encoding.UTF8.GetBytes(
            "[{\"timeSeries\":[{\"areas\":[{\"area\":{\"name\":\"他県\"},\"weathers\":[]}]}]}]");
        var http = new FakeHttpClient(_ => json);
        var source = new JmaWeatherSource("130000", "東京地方", http);

        var ex = await Assert.ThrowsAsync<ResearchException>(() => source.FetchAsync());
        Assert.Equal("E-WX-FETCH-FAILED-001", ex.Code);
    }
}
