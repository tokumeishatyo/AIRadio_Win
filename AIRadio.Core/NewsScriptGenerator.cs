namespace AIRadio.Core;

/// <summary>
/// ニュース素材（見出し・天気）からアナウンサー原稿の**本文**を LLM 生成するための純粋ロジック（W11）。
/// 時報イントロ・締めのアウトロは固定文（正確性が必要）のため LLM に書かせない。LLM 呼び出し自体は
/// Infrastructure 側（<c>LlmNewsScriptProvider</c>）が行い、本クラスはプロンプト構築と整形のみを担う。
/// Mac 版 <c>NewsScriptGenerator</c>（enum-namespace）の移植。
/// </summary>
public static class NewsScriptGenerator
{
    /// <summary>
    /// 素材と読み手ペルソナからニュース本文生成の <see cref="LLMRequest"/> を組み立てる。出力契約は
    /// 「読み上げる本文のみ（見出し番号・記号装飾・定型句・時刻日付を書かない）」。長さは
    /// <paramref name="targetCharacters"/> 文字以上、<c>targetCharacters * 12 / 10</c>（**整数除算**）文字以内。
    /// </summary>
    public static LLMRequest MakeRequest(
        string news,
        string weather,
        string persona,
        int targetCharacters,
        string styleHint = "",
        double temperature = 0.6)
    {
        var constraints =
            "- 出力は読み上げる本文のみ。見出しの番号、記号装飾、ナレーション指示は書かない。\n" +
            "- 各ニュースを自然な語りでつなぎ、ところどころに短いコメントをひとこと添える。\n" +
            "- 天気予報にも一言添える。\n" +
            "- 挨拶、時刻や日付、「ニュースの時間です」「以上です」などの定型句は書かない（前後に固定文があるため）。\n" +
            $"- 合計 {targetCharacters} 文字以上、{targetCharacters * 12 / 10} 文字以内。";
        if (styleHint.Length > 0)
        {
            constraints += $"\n- 語りのスタイル: {styleHint}";
        }

        var prompt =
            "以下の素材から、ラジオで読み上げるニュース原稿の本文を書いてください。\n\n" +
            "# 本日のニュース見出し\n" +
            $"{news}\n\n" +
            "# 天気予報\n" +
            $"{weather}\n\n" +
            "# 制約\n" +
            constraints;

        var system =
            "あなたはラジオ番組「ケイラボAIラジオ」のニュースキャスターです。\n" +
            persona;

        return new LLMRequest(prompt, System: system, Temperature: temperature);
    }

    /// <summary>
    /// 生成結果の整形: 行頭マークダウン装飾（<c>#</c>/<c>*</c>/<c>-</c>/<c>&gt;</c>/<c>•</c>）と <c>**</c> を除去し、
    /// 空行を落として 1 行に連結（半角スペース区切り）。実質空なら <c>E-LLM-EMPTY-RESPONSE-001</c>。
    /// 横方向空白のみ除去（<see cref="Whitespace.TrimHorizontal"/>＝Mac の <c>.whitespaces</c> 相当）で改行類は保持しない前提。
    /// </summary>
    public static string Sanitize(string raw)
    {
        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(StripLine)
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            throw LlmException.EmptyResponse();
        }
        return string.Join(" ", lines);
    }

    private static string StripLine(string rawLine)
    {
        var text = Whitespace.TrimHorizontal(rawLine);
        // 行頭の装飾（箇条書き・見出し・引用・強調）を、装飾文字でなくなるまで剥がす（Mac と同じ明示文字集合）。
        while (text.Length > 0 && "#*->•".Contains(text[0]))
        {
            text = Whitespace.TrimHorizontal(text[1..]);
        }
        return text.Replace("**", "");
    }
}
