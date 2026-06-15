namespace AIRadio.Core;

/// <summary>
/// {key} 形式のプレースホルダを値で置換する純粋ユーティリティ。
/// オープニング前口上・コーナーテンプレ等の展開に使う土台。
/// 未知のプレースホルダ（values に無いキー）は原文のまま残す。
/// </summary>
public static class TemplateExpander
{
    public static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value);
        }
        return result;
    }
}
