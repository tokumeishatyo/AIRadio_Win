using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>W13.5 曜日替わり編成（<see cref="WeeklyCast"/>）。確定表・既定・タイムゾーン境界を固定。</summary>
public class WeeklyCastTests
{
    // 日本標準時（DST なし固定 +9。システム TZ データベースに依存しない）。
    private static readonly TimeZoneInfo Jst =
        TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    [Fact]
    public void Standard_MatchesConfirmedTable_SundayIsThree()
    {
        var s = WeeklyCast.Standard;
        Assert.Equal(new[] { "zundamon", "metan", "tsumugi" }, s.Casts[DayOfWeek.Sunday]);
        Assert.Equal(new[] { "zundamon", "metan" }, s.Casts[DayOfWeek.Monday]);
        Assert.Equal(new[] { "metan", "tsumugi" }, s.Casts[DayOfWeek.Tuesday]);
        Assert.Equal(new[] { "tsumugi", "zundamon" }, s.Casts[DayOfWeek.Wednesday]);
        Assert.Equal(new[] { "zundamon", "metan" }, s.Casts[DayOfWeek.Thursday]);
        Assert.Equal(new[] { "metan", "tsumugi" }, s.Casts[DayOfWeek.Friday]);
        Assert.Equal(new[] { "tsumugi", "zundamon" }, s.Casts[DayOfWeek.Saturday]);
        Assert.Equal(3, s.Casts[DayOfWeek.Sunday].Count); // 日曜は 3 人運営
    }

    [Fact]
    public void DjIds_And_MainDjId_FollowTheDaysCast()
    {
        // その日（JST 正午）の cast 先頭がメイン。日付の曜日に紐づく。
        var date = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.FromHours(9));
        var day = date.DayOfWeek;
        Assert.Equal(WeeklyCast.Standard.Casts[day], WeeklyCast.Standard.DjIds(date, Jst));
        Assert.Equal(WeeklyCast.Standard.Casts[day][0], WeeklyCast.Standard.MainDjId(date, Jst));
    }

    [Fact]
    public void DjIds_RespectsTimeZone_AtDayBoundary()
    {
        // 23:00 UTC は JST（+9）で翌日 08:00 → 曜日が変わる。tz で解決曜日が変わることを固定。
        var instant = new DateTimeOffset(2026, 6, 14, 23, 0, 0, TimeSpan.Zero);
        var utcDay = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Utc).DayOfWeek;
        var jstDay = TimeZoneInfo.ConvertTime(instant, Jst).DayOfWeek;

        Assert.NotEqual(utcDay, jstDay);
        Assert.Equal(WeeklyCast.Standard.Casts[utcDay], WeeklyCast.Standard.DjIds(instant, TimeZoneInfo.Utc));
        Assert.Equal(WeeklyCast.Standard.Casts[jstDay], WeeklyCast.Standard.DjIds(instant, Jst));
    }

    [Fact]
    public void UndefinedDay_ReturnsEmpty_AndNullMain()
    {
        var cast = new WeeklyCast(new Dictionary<DayOfWeek, IReadOnlyList<string>>());
        var date = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(cast.DjIds(date, TimeZoneInfo.Utc));
        Assert.Null(cast.MainDjId(date, TimeZoneInfo.Utc));
    }
}
