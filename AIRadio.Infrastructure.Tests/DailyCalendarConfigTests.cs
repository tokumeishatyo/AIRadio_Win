using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W17 calendar.yaml ローダ（Mac CalendarConfig.swift 逐語）。weekday_names は null のみ Standard・present(空配列含む)は 7 要素必須、
/// significance 省略=Low・case-sensitive、date は month 1-12 / day 1-31 の単純範囲。
/// </summary>
public class DailyCalendarConfigTests
{
    private const string SevenNames =
        "weekday_names: [\"日曜日\",\"月曜日\",\"火曜日\",\"水曜日\",\"木曜日\",\"金曜日\",\"土曜日\"]\n";

    [Fact]
    public void Loads_WeekdayNames_AndAnniversaries()
    {
        var cal = DailyCalendarConfig.FromYaml(
            SevenNames + "anniversaries:\n  - { date: \"05-05\", name: \"こどもの日\", significance: high }\n");
        Assert.Equal(7, cal.WeekdayNames.Count);
        Assert.Equal(new Anniversary(5, 5, "こどもの日", AnniversarySignificance.High), cal.Anniversaries.Single());
    }

    [Fact]
    public void WeekdayNames_Omitted_UsesStandard()
    {
        var cal = DailyCalendarConfig.FromYaml("anniversaries: []\n");
        Assert.Equal(DailyCalendar.StandardWeekdayNames, cal.WeekdayNames);
    }

    [Theory]
    [InlineData("weekday_names: []\n")]                    // present・空配列（count 0）→ Mac は throw
    [InlineData("weekday_names: [\"日\",\"月\",\"火\"]\n")]   // count != 7
    public void WeekdayNames_PresentWrongCount_Throws(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => DailyCalendarConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Anniversaries_Omitted_Empty()
        => Assert.Empty(DailyCalendarConfig.FromYaml(SevenNames).Anniversaries);

    [Fact]
    public void Significance_Omitted_DefaultsToLow()
    {
        // Mac significanceDefaultsToLow 相当（省略は throw せず Low）。
        var cal = DailyCalendarConfig.FromYaml("anniversaries:\n  - { date: \"07-07\", name: \"七夕\" }\n");
        Assert.Equal(AnniversarySignificance.Low, cal.Anniversaries.Single().Significance);
    }

    [Theory]
    [InlineData("anniversaries:\n  - { date: \"07-07\", name: \"x\", significance: medium }\n")] // 非空未知
    [InlineData("anniversaries:\n  - { date: \"07-07\", name: \"x\", significance: High }\n")]   // 大文字（case-sensitive）
    [InlineData("anniversaries:\n  - { date: \"0707\", name: \"x\" }\n")]                          // 形式不正（部数≠2）
    [InlineData("anniversaries:\n  - { date: \"ab-cd\", name: \"x\" }\n")]                         // 非整数
    [InlineData("anniversaries:\n  - { date: \"13-01\", name: \"x\" }\n")]                         // month 範囲外
    [InlineData("anniversaries:\n  - { date: \"05-32\", name: \"x\" }\n")]                         // day 範囲外
    [InlineData("anniversaries:\n  - { date: \"07-07\" }\n")]                                      // name 欠落
    public void InvalidEntry_ThrowsMissingField(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => DailyCalendarConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void BrokenYaml_Throws()
        => Assert.ThrowsAny<Exception>(() => DailyCalendarConfig.FromYaml("weekday_names: [unclosed\n"));
}
