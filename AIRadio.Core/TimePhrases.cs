using System.Globalization;

namespace AIRadio.Core;

/// <summary>
/// 時間帯の区分（境界はハードコード）。Mac 版 <c>TimeOfDay</c>（<c>TimePhrases.swift</c>）の移植。
/// 朝 05:00–11:59 / 昼 12:00–16:59 / 夜 17:00–04:59（深夜またぎ）。
/// </summary>
public enum TimeOfDay
{
    /// <summary>朝（05:00–11:59）。「おはようございます」。</summary>
    Morning,

    /// <summary>昼（12:00–16:59）。「こんにちは」。</summary>
    Afternoon,

    /// <summary>夜（17:00–04:59、深夜またぎ）。「こんばんは」。</summary>
    Evening,
}

/// <summary>24 時間制の時（0–23）から <see cref="TimeOfDay"/> を判定する（Mac 版 <c>TimeOfDay.of(hour:)</c> 相当）。</summary>
public static class TimeOfDayClassifier
{
    /// <summary>朝 <c>5..11</c> / 昼 <c>12..16</c> / 夜 それ以外（17–23 と 0–4 が evening＝深夜またぎ）。</summary>
    public static TimeOfDay Of(int hour24) => hour24 switch
    {
        >= 5 and <= 11 => TimeOfDay.Morning,
        >= 12 and <= 16 => TimeOfDay.Afternoon,
        _ => TimeOfDay.Evening,
    };
}

/// <summary>
/// 時間帯ごとの挨拶文字列（<c>config/themes.yaml</c> の <c>greetings:</c>）。Mac 版 <c>Greetings</c> struct の移植。
/// </summary>
/// <param name="Morning">朝の挨拶。</param>
/// <param name="Afternoon">昼の挨拶。</param>
/// <param name="Evening">夜の挨拶。</param>
public sealed record Greetings(
    string Morning = "おはようございます",
    string Afternoon = "こんにちは",
    string Evening = "こんばんは")
{
    /// <summary>時間帯に対応する挨拶を返す。</summary>
    public string TextFor(TimeOfDay timeOfDay) => timeOfDay switch
    {
        TimeOfDay.Morning => Morning,
        TimeOfDay.Afternoon => Afternoon,
        _ => Evening,
    };
}

/// <summary>
/// 現在時刻からアナウンス用プレースホルダ値を生成する純粋ロジック（仕様 w8 §3）。
/// <see cref="TemplateExpander"/> と組で使い、**発話直前に**展開することで時報として正確になる。
/// Mac 版 <c>TimePhrases.values(date:greetings:timeZone:)</c> の移植。
/// </summary>
public static class TimePhrases
{
    /// <summary>
    /// 時刻プレースホルダ値辞書を返す。キーは <c>greeting</c> / <c>month</c> / <c>day</c> / <c>ampm</c> /
    /// <c>hour</c> / <c>hour12</c> / <c>minute</c>。数値は**ゼロ埋めなし**（不変カルチャ）。
    /// <list type="bullet">
    ///   <item><c>{greeting}</c>: 時間帯挨拶（<paramref name="greetings"/> から）。</item>
    ///   <item><c>{ampm}</c> + <c>{hour}</c>: NHK 式 12 時間（午前/午後 + <c>hour24 % 12</c>＝0–11。12:xx は「午後0時」）。</item>
    ///   <item><c>{hour12}</c>: 午前/午後なしの 12 時間表記（0:xx→0、12:xx→12、それ以外 1–11）。</item>
    /// </list>
    /// </summary>
    /// <param name="date">対象時刻（通常は <see cref="IClock.Now"/>＝発話直前の現在時刻）。</param>
    /// <param name="greetings">時間帯挨拶（省略時は既定の挨拶）。</param>
    /// <param name="timeZone">解釈に使うタイムゾーン（省略時は <see cref="TimeZoneInfo.Local"/>）。</param>
    public static IReadOnlyDictionary<string, string> Values(
        DateTimeOffset date,
        Greetings? greetings = null,
        TimeZoneInfo? timeZone = null)
    {
        greetings ??= new Greetings();
        timeZone ??= TimeZoneInfo.Local;

        var local = TimeZoneInfo.ConvertTime(date, timeZone);
        var hour24 = local.Hour;
        var hour12NoAmpm = hour24 == 0 ? 0 : (hour24 % 12 == 0 ? 12 : hour24 % 12);
        var inv = CultureInfo.InvariantCulture;

        return new Dictionary<string, string>
        {
            ["greeting"] = greetings.TextFor(TimeOfDayClassifier.Of(hour24)),
            ["month"] = local.Month.ToString(inv),
            ["day"] = local.Day.ToString(inv),
            ["ampm"] = hour24 < 12 ? "午前" : "午後",
            ["hour"] = (hour24 % 12).ToString(inv),
            ["hour12"] = hour12NoAmpm.ToString(inv),
            ["minute"] = local.Minute.ToString(inv),
        };
    }
}
