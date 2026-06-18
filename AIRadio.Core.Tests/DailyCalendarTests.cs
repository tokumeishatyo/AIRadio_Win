using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>W17 DailyCalendar（曜日名・記念日の軽重）。Context は固定日付・UTC で決定論。Mac DailyCalendar.swift 逐語移植。</summary>
public class DailyCalendarTests
{
    // 既知の曜日アンカー（UTC）: 1970-01-01=木曜 / 2000-01-01=土曜 / 2023-01-01=日曜。
    private static DateTimeOffset Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Context_NoAnniversary_WeekdayAndSeasonOnly()
    {
        var cal = new DailyCalendar(); // 標準曜日名・記念日なし
        Assert.Equal("今日は1月1日（木曜日）、冬、正月明けです。", cal.Context(Utc(1970, 1, 1), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Context_High_AddsProgramWideClause()
    {
        var cal = new DailyCalendar(anniversaries: new[] { new Anniversary(5, 5, "こどもの日", AnniversarySignificance.High) });
        var text = cal.Context(Utc(2000, 5, 5), TimeZoneInfo.Utc);
        Assert.Contains("今日は『こどもの日』。番組を通して、こどもの日にちなんだ話題を意識して織り込んでください。", text);
    }

    [Fact]
    public void Context_Low_AddsLightClause()
    {
        var cal = new DailyCalendar(anniversaries: new[] { new Anniversary(7, 7, "七夕", AnniversarySignificance.Low) });
        var text = cal.Context(Utc(2000, 7, 7), TimeZoneInfo.Utc);
        Assert.Contains("今日は『七夕』。話の流れで軽く触れる程度にとどめてください。", text);
    }

    [Fact]
    public void Context_MultipleSameDay_PrefersHigh()
    {
        var cal = new DailyCalendar(anniversaries: new[]
        {
            new Anniversary(3, 3, "ひな祭り", AnniversarySignificance.Low),
            new Anniversary(3, 3, "耳の日", AnniversarySignificance.High),
        });
        var text = cal.Context(Utc(2000, 3, 3), TimeZoneInfo.Utc);
        Assert.Contains("今日は『耳の日』。番組を通して、耳の日にちなんだ話題を意識して織り込んでください。", text);
        Assert.DoesNotContain("ひな祭り", text); // 代表 1 件・low は出さない
    }

    [Theory]
    [InlineData(2023, 1, 1, "日曜日")] // (int)DayOfWeek.Sunday = 0
    [InlineData(1970, 1, 1, "木曜日")] // (int)DayOfWeek.Thursday = 4
    [InlineData(2000, 1, 1, "土曜日")] // (int)DayOfWeek.Saturday = 6（端境界）
    public void Context_WeekdayName_FromList(int y, int m, int d, string expected)
    {
        var text = new DailyCalendar().Context(Utc(y, m, d), TimeZoneInfo.Utc);
        Assert.Contains($"（{expected}）", text);
    }

    [Fact]
    public void Standard_HasWeekdayNames_NoAnniversaries()
    {
        Assert.Equal(7, DailyCalendar.Standard.WeekdayNames.Count);
        Assert.Equal("日曜日", DailyCalendar.Standard.WeekdayNames[0]);
        Assert.Empty(DailyCalendar.Standard.Anniversaries);
    }
}
