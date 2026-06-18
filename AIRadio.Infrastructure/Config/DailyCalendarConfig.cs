using System.Globalization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/calendar.yaml</c> のローダ（曜日名 + 記念日 → <see cref="DailyCalendar"/>。W17）。Mac <c>DailyCalendarLoader</c>（CalendarConfig.swift）逐語移植。
/// <c>weekday_names</c> は<b>キー省略時のみ</b>標準曜日名、present（空配列 <c>[]</c> 含む）は 7 要素必須。<c>anniversaries</c> 省略は空。
/// <c>significance</c> 省略は Low 既定（throw しない）。壊れ・不正値（present≠7 / date 形式・範囲 / name 欠落 / significance 非空未知）は fail-fast（<c>E-CFG-MISSING-FIELD-001</c>）。
/// </summary>
public static class DailyCalendarConfig
{
    public static DailyCalendar FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);

        IReadOnlyList<string> weekdayNames;
        if (dto?.WeekdayNames is { } names)
        {
            // present（空配列含む）は 7 要素必須（Mac CalendarConfig.swift:22-29 一致。null のみ Standard）。
            if (names.Count != 7)
            {
                throw ConfigException.MissingField($"calendar.weekday_names は 7 要素にしてください（現在 {names.Count}）");
            }
            weekdayNames = names;
        }
        else
        {
            weekdayNames = DailyCalendar.StandardWeekdayNames;
        }

        var anniversaries = (dto?.Anniversaries ?? new List<AnniversaryDto>()).Select(Map).ToList();
        return new DailyCalendar(weekdayNames, anniversaries);
    }

    public static DailyCalendar LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static Anniversary Map(AnniversaryDto entry)
    {
        if (string.IsNullOrEmpty(entry.Date) || string.IsNullOrEmpty(entry.Name))
        {
            throw ConfigException.MissingField("calendar.anniversaries[].date / name は必須です");
        }
        var parts = entry.Date.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
            || month < 1 || month > 12 || day < 1 || day > 31)
        {
            throw ConfigException.MissingField($"calendar.anniversaries[].date は MM-DD 形式（month 1-12 / day 1-31）: {entry.Date}");
        }
        // significance は case-sensitive（Mac 一致）。省略/null は Low 既定。high/low/省略 以外の非空文字列は fail-fast。
        // C# enum 既定は宣言順先頭（High）のため、null 時は明示的に Low を代入する。
        var significance = entry.Significance switch
        {
            "high" => AnniversarySignificance.High,
            "low" or null => AnniversarySignificance.Low,
            _ => throw ConfigException.MissingField($"calendar.anniversaries[].significance は high / low: {entry.Significance}"),
        };
        return new Anniversary(month, day, entry.Name, significance);
    }

    public sealed class Dto
    {
        public List<string>? WeekdayNames { get; set; }
        public List<AnniversaryDto>? Anniversaries { get; set; }
    }

    public sealed class AnniversaryDto
    {
        public string? Date { get; set; }
        public string? Name { get; set; }
        public string? Significance { get; set; }
    }
}
