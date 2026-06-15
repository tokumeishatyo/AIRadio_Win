using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W8 時刻プレースホルダ生成の純粋ロジック（<see cref="TimeOfDayClassifier"/> / <see cref="Greetings"/> /
/// <see cref="TimePhrases"/>）。Mac 版 <c>TimePhrases.swift</c> 移植の忠実性を固定時刻 + 固定 TZ で決定論的に検証する。
/// </summary>
public class TimePhrasesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Utc0(int month, int day, int hour, int minute) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // --- TimeOfDayClassifier.Of: 境界（朝 5..11 / 昼 12..16 / 夜 それ以外＝17–23・0–4 の深夜またぎ） ---

    [Theory]
    [InlineData(0, TimeOfDay.Evening)]   // 深夜
    [InlineData(4, TimeOfDay.Evening)]   // 夜の上端（深夜またぎ）
    [InlineData(5, TimeOfDay.Morning)]   // 朝の下端
    [InlineData(11, TimeOfDay.Morning)]  // 朝の上端
    [InlineData(12, TimeOfDay.Afternoon)] // 昼の下端
    [InlineData(16, TimeOfDay.Afternoon)] // 昼の上端
    [InlineData(17, TimeOfDay.Evening)]  // 夜の下端
    [InlineData(23, TimeOfDay.Evening)]  // 夜
    public void TimeOfDay_Of_ClassifiesByHour(int hour24, TimeOfDay expected)
        => Assert.Equal(expected, TimeOfDayClassifier.Of(hour24));

    // --- Greetings.TextFor ---

    [Fact]
    public void Greetings_TextFor_MapsEachTimeOfDay()
    {
        var g = new Greetings("お", "ひ", "ば");
        Assert.Equal("お", g.TextFor(TimeOfDay.Morning));
        Assert.Equal("ひ", g.TextFor(TimeOfDay.Afternoon));
        Assert.Equal("ば", g.TextFor(TimeOfDay.Evening));
    }

    [Fact]
    public void Greetings_Defaults_AreStandardJapanese()
    {
        var g = new Greetings();
        Assert.Equal("おはようございます", g.Morning);
        Assert.Equal("こんにちは", g.Afternoon);
        Assert.Equal("こんばんは", g.Evening);
    }

    // --- TimePhrases.Values: 各キー ---

    [Fact]
    public void Values_Afternoon_AllKeys()
    {
        var v = TimePhrases.Values(Utc0(6, 12, 15, 7), new Greetings(), Utc);
        Assert.Equal("こんにちは", v["greeting"]);
        Assert.Equal("6", v["month"]);
        Assert.Equal("12", v["day"]);
        Assert.Equal("午後", v["ampm"]);
        Assert.Equal("3", v["hour"]);
        Assert.Equal("3", v["hour12"]);
        Assert.Equal("7", v["minute"]);
    }

    [Fact]
    public void Values_Morning_NoZeroPadding()
    {
        var v = TimePhrases.Values(Utc0(1, 3, 9, 5), new Greetings(), Utc);
        Assert.Equal("おはようございます", v["greeting"]);
        Assert.Equal("1", v["month"]);     // ゼロ埋めなし
        Assert.Equal("3", v["day"]);
        Assert.Equal("午前", v["ampm"]);
        Assert.Equal("9", v["hour"]);
        Assert.Equal("9", v["hour12"]);
        Assert.Equal("5", v["minute"]);    // ゼロ埋めなし
    }

    [Fact]
    public void Values_Noon_HourIsZero_Hour12IsTwelve()
    {
        // 正午: {hour}=hour24%12=0（午後0時）/ {hour12}=12。両者が分岐する唯一の時刻。
        var v = TimePhrases.Values(Utc0(8, 1, 12, 0), new Greetings(), Utc);
        Assert.Equal("午後", v["ampm"]);
        Assert.Equal("0", v["hour"]);
        Assert.Equal("12", v["hour12"]);
        Assert.Equal("こんにちは", v["greeting"]); // 12 時は昼
    }

    [Fact]
    public void Values_Midnight_BothHoursZero_EveningGreeting()
    {
        // 深夜 0:30: {hour}=0 / {hour12}=0（両方 0）/ 挨拶は夜（深夜またぎ）。
        var v = TimePhrases.Values(Utc0(12, 31, 0, 30), new Greetings(), Utc);
        Assert.Equal("午前", v["ampm"]);
        Assert.Equal("0", v["hour"]);
        Assert.Equal("0", v["hour12"]);
        Assert.Equal("30", v["minute"]);
        Assert.Equal("こんばんは", v["greeting"]);
    }

    [Fact]
    public void Values_Evening_PmHours()
    {
        var v = TimePhrases.Values(Utc0(6, 12, 20, 45), new Greetings(), Utc);
        Assert.Equal("午後", v["ampm"]);
        Assert.Equal("8", v["hour"]);   // 20 % 12
        Assert.Equal("8", v["hour12"]);
        Assert.Equal("こんばんは", v["greeting"]);
    }

    [Fact]
    public void Values_UsesCustomGreetings()
    {
        var v = TimePhrases.Values(Utc0(6, 12, 9, 0), new Greetings(Morning: "おはやっす"), Utc);
        Assert.Equal("おはやっす", v["greeting"]);
    }

    [Fact]
    public void Values_DefaultGreetings_WhenOmitted()
    {
        var v = TimePhrases.Values(Utc0(6, 12, 9, 0), timeZone: Utc);
        Assert.Equal("おはようございます", v["greeting"]);
    }

    [Fact]
    public void Values_DefaultTimeZone_ProducesWellFormedDict()
    {
        // 既定 timeZone（Local）経路（App 実配線で使う）の健全性。
        // 具体値は環境依存のため非決定性を避け、不変条件のみ検証する。
        var v = TimePhrases.Values(Utc0(6, 12, 15, 7)); // timeZone 省略 → Local

        Assert.Equal(
            new[] { "ampm", "day", "greeting", "hour", "hour12", "minute", "month" },
            v.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.All(v.Values, s => Assert.False(string.IsNullOrEmpty(s)));
        Assert.InRange(int.Parse(v["hour"]), 0, 11);
        Assert.Contains(v["ampm"], new[] { "午前", "午後" });
    }

    [Fact]
    public void Values_RespectsTimeZone()
    {
        // 同じ瞬間 15:00Z を、UTC と +09:00 で解釈すると時・日・時間帯が変わる。
        var instant = Utc0(6, 12, 15, 0);
        var plus9 = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");

        var utc = TimePhrases.Values(instant, new Greetings(), Utc);
        Assert.Equal("午後", utc["ampm"]);       // 15:00Z ＝ 午後3時
        Assert.Equal("3", utc["hour"]);
        Assert.Equal("12", utc["day"]);
        Assert.Equal("こんにちは", utc["greeting"]);

        var jst = TimePhrases.Values(instant, new Greetings(), plus9);
        Assert.Equal("午前", jst["ampm"]);       // 15:00Z + 9h = 00:00（翌日）
        Assert.Equal("0", jst["hour"]);
        Assert.Equal("13", jst["day"]);          // 日付がまたぐ
        Assert.Equal("こんばんは", jst["greeting"]); // 深夜は夜扱い
    }
}
