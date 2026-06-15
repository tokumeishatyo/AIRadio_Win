using System.Globalization;

namespace AIRadio.Core;

/// <summary>
/// Swift の <c>CharacterSet.whitespaces</c> 相当のトリム（Unicode Zs カテゴリ + 水平タブのみ。
/// 改行類 LF/CR/VT/FF/NEL/LS/PS は**保持**する）。Mac 版が <c>.whitespacesAndNewlines</c> ではなく
/// <c>.whitespaces</c> を使う箇所（台本/候補パース・チャンク分割など）の移植に用いる共通ヘルパ。
/// </summary>
internal static class Whitespace
{
    public static string TrimHorizontal(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsHorizontal(s[start]))
        {
            start++;
        }
        while (end > start && IsHorizontal(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }

    private static bool IsHorizontal(char c)
        => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
}
