using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>W12 季節コンテキスト（<see cref="SeasonPhrases"/>）の Mac 移植忠実性。月→季節 + 日付文の書式。</summary>
public class SeasonPhrasesTests
{
    [Theory]
    [InlineData(1, "冬、正月明け")]
    [InlineData(2, "冬の終わり")]
    [InlineData(3, "春の始まり")]
    [InlineData(4, "春")]
    [InlineData(5, "初夏、新緑の季節")]
    [InlineData(6, "梅雨の時期")]
    [InlineData(7, "盛夏")]
    [InlineData(8, "真夏、残暑")]
    [InlineData(9, "初秋")]
    [InlineData(10, "秋")]
    [InlineData(11, "晩秋")]
    [InlineData(12, "冬、年の瀬")]
    public void Phrase_ReturnsSeasonForEachMonth(int month, string expected)
        => Assert.Equal(expected, SeasonPhrases.Phrase(month));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Phrase_OutOfRange_FallsToYearEnd(int month)
        => Assert.Equal("冬、年の瀬", SeasonPhrases.Phrase(month));

    [Fact]
    public void DateContext_FormatsDateAndSeason()
    {
        var date = new DateTimeOffset(2026, 6, 12, 15, 7, 0, TimeSpan.Zero);
        Assert.Equal("今日は6月12日、梅雨の時期です。", SeasonPhrases.DateContext(date, TimeZoneInfo.Utc));
    }

    [Fact]
    public void DateContext_SingleDigitMonthDay_NoZeroPadding()
    {
        var date = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("今日は1月3日、冬、正月明けです。", SeasonPhrases.DateContext(date, TimeZoneInfo.Utc));
    }

    [Fact]
    public void DateContext_RespectsTimeZone()
    {
        // 6/12 23:00Z を +09:00 で見ると 6/13。
        var instant = new DateTimeOffset(2026, 6, 12, 23, 0, 0, TimeSpan.Zero);
        var plus9 = TimeZoneInfo.CreateCustomTimeZone("t+9", TimeSpan.FromHours(9), "t+9", "t+9");
        Assert.Equal("今日は6月13日、梅雨の時期です。", SeasonPhrases.DateContext(instant, plus9));
    }
}
