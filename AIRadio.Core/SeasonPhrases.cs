using System.Globalization;

namespace AIRadio.Core;

/// <summary>
/// 月 → 季節の言い回し（仕様 w12 §4、境界はハードコード）。台本生成プロンプトに日付・季節を注入し、
/// 「6 月に春めいた話」のような季節ズレを防ぐ。Mac 版 <c>SeasonPhrases</c> 移植。
/// （記念日の軽重を含む本格版 <c>DailyContext</c> は W17。本スライスは月→季節のみ。）
/// </summary>
public static class SeasonPhrases
{
    /// <summary>月（1〜12）に対応する季節の言い回し。12 および範囲外は「冬、年の瀬」（Mac の default 相当）。</summary>
    public static string Phrase(int month) => month switch
    {
        1 => "冬、正月明け",
        2 => "冬の終わり",
        3 => "春の始まり",
        4 => "春",
        5 => "初夏、新緑の季節",
        6 => "梅雨の時期",
        7 => "盛夏",
        8 => "真夏、残暑",
        9 => "初秋",
        10 => "秋",
        11 => "晩秋",
        _ => "冬、年の瀬",
    };

    /// <summary>
    /// プロンプト注入用の日付コンテキスト（例「今日は6月12日、梅雨の時期です。」）。月日は**ゼロ埋めなし**。
    /// </summary>
    /// <param name="date">対象時刻（通常は <see cref="IClock.Now"/>）。</param>
    /// <param name="timeZone">解釈に使うタイムゾーン（省略時は <see cref="TimeZoneInfo.Local"/>）。</param>
    public static string DateContext(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local);
        var inv = CultureInfo.InvariantCulture;
        return $"今日は{local.Month.ToString(inv)}月{local.Day.ToString(inv)}日、{Phrase(local.Month)}です。";
    }
}
