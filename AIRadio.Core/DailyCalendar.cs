using System.Globalization;

namespace AIRadio.Core;

/// <summary>記念日の重要度（仕様 W17）。High = 祝日級・番組全体に波及／Low = 軽く触れる程度。Mac <c>AnniversarySignificance</c> 移植。</summary>
public enum AnniversarySignificance
{
    High,
    Low,
}

/// <summary>記念日 1 件（<c>config/calendar.yaml</c>。仕様 W17）。月日で一致判定する。Mac <c>Anniversary</c> 移植。</summary>
public sealed record Anniversary(int Month, int Day, string Name, AnniversarySignificance Significance);

/// <summary>
/// 暦コンテキスト（曜日名 + 記念日）。台本生成プロンプトに注入する「日付・曜日・季節・記念日」の文を生成する（仕様 W17）。
/// 季節は <see cref="SeasonPhrases.Phrase(int)"/> を流用。<c>config/calendar.yaml</c> から読み込む（<c>DailyCalendarConfig</c>）。Mac <c>DailyCalendar</c> 移植。
/// 曜日は C# <see cref="DayOfWeek"/>（Sunday=0 … Saturday=6）を直接 index する（Mac の Calendar weekday-1 と等価。-1 不要）。
/// </summary>
public sealed class DailyCalendar
{
    /// <summary>曜日名（index = <see cref="DayOfWeek"/>。0=日曜 … 6=土曜）。</summary>
    public IReadOnlyList<string> WeekdayNames { get; }

    public IReadOnlyList<Anniversary> Anniversaries { get; }

    public DailyCalendar(IReadOnlyList<string>? weekdayNames = null, IReadOnlyList<Anniversary>? anniversaries = null)
    {
        WeekdayNames = weekdayNames ?? StandardWeekdayNames;
        Anniversaries = anniversaries ?? Array.Empty<Anniversary>();
    }

    /// <summary>標準の日本語曜日名（<see cref="DayOfWeek"/> 0=日曜 … 6=土曜の順）。config 省略時のフォールバック。</summary>
    public static IReadOnlyList<string> StandardWeekdayNames { get; } =
        new[] { "日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日" };

    /// <summary>config 省略時の既定（標準曜日名・記念日なし）。</summary>
    public static DailyCalendar Standard { get; } = new();

    /// <summary>
    /// プロンプト注入用の日付コンテキスト（例「今日は6月14日（土曜日）、梅雨の時期です。」）。記念日があれば重要度に応じた一文を付す。
    /// 月日は<b>ゼロ埋めなし</b>（<see cref="CultureInfo.InvariantCulture"/>）。
    /// </summary>
    public string Context(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local);
        var inv = CultureInfo.InvariantCulture;
        var month = local.Month;
        var day = local.Day;
        var weekdayIndex = (int)local.DayOfWeek; // Sunday=0 … Saturday=6
        var weekday = weekdayIndex >= 0 && weekdayIndex < WeekdayNames.Count ? WeekdayNames[weekdayIndex] : "";

        var text = $"今日は{month.ToString(inv)}月{day.ToString(inv)}日";
        if (weekday.Length > 0)
        {
            text += $"（{weekday}）";
        }
        text += $"、{SeasonPhrases.Phrase(month)}です。";

        var anniversary = AnniversaryFor(month, day);
        if (anniversary is not null)
        {
            text += anniversary.Significance switch
            {
                AnniversarySignificance.High =>
                    $"今日は『{anniversary.Name}』。番組を通して、{anniversary.Name}にちなんだ話題を意識して織り込んでください。",
                _ => $"今日は『{anniversary.Name}』。話の流れで軽く触れる程度にとどめてください。",
            };
        }
        return text;
    }

    /// <summary>その月日の記念日（複数一致なら High を優先し、代表 1 件。仕様 W17 §3）。</summary>
    private Anniversary? AnniversaryFor(int month, int day)
    {
        var matches = Anniversaries.Where(a => a.Month == month && a.Day == day).ToList();
        return matches.FirstOrDefault(a => a.Significance == AnniversarySignificance.High) ?? matches.FirstOrDefault();
    }
}
