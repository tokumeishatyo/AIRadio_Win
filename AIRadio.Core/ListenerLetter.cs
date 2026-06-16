namespace AIRadio.Core;

/// <summary>架空のリスナーからのお便り（仕様 w12 §3）。</summary>
public sealed record ListenerLetter(string RadioName, string Body);

/// <summary>
/// お便りコーナー用に、架空リスナーのお便りを LLM 生成する純粋ロジック。Mac 版 <c>ListenerLetterGenerator</c> 移植。
/// 出力契約: 1 行目 = ラジオネーム、2 行目以降 = 本文。装飾は除去、本文空ならパース失敗（<c>E-LLM-SCRIPT-PARSE-FAILED-001</c>）。
/// </summary>
public sealed class ListenerLetterGenerator
{
    private readonly ILLMBackend _llm;
    private readonly double _temperature;

    public ListenerLetterGenerator(ILLMBackend llm, double temperature = 0.9)
    {
        _llm = llm;
        _temperature = temperature;
    }

    public async Task<ListenerLetter> GenerateAsync(string theme, string dateContext, CancellationToken ct = default)
    {
        var raw = await _llm.GenerateAsync(MakeRequest(theme, dateContext, _temperature), ct).ConfigureAwait(false);
        return Parse(raw);
    }

    // MARK: - プロンプト構築

    public static LLMRequest MakeRequest(string theme, string dateContext, double temperature = 0.9)
    {
        var prompt =
            "ラジオ番組「ケイラボAIラジオ」に届いた、架空のリスナーからのお便りを 1 通書いてください。\n\n" +
            "# テーマ\n" +
            $"{theme}\n\n" +
            "# 今日の日付と季節\n" +
            $"{dateContext}\n\n" +
            "# 制約\n" +
            "- 1 行目はラジオネーム（ペンネーム）のみを書く。\n" +
            "- 2 行目以降にお便りの本文を書く（200 文字以上、400 文字以内。テーマにまつわる日常の出来事・気づき・ちょっとした相談など）。\n" +
            "- 季節や時候の話は、上の日付・季節に合わせる。\n" +
            "- 出力はお便りのみ。見出し、説明、記号装飾は書かない。";
        return new LLMRequest(prompt, Temperature: temperature);
    }

    // MARK: - パース

    public static ListenerLetter Parse(string raw)
    {
        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Strip)
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            throw LlmException.ScriptParseFailed("お便りが空です");
        }

        var radioName = lines[0];
        // 「ラジオネーム: ◯◯」のラベル付き出力も許容する（Mac と同じラベル・同じ順序）。
        foreach (var label in new[] { "ラジオネーム", "ラジオネーム名" })
        {
            if (radioName.StartsWith(label, StringComparison.Ordinal))
            {
                var rest = radioName[label.Length..].Trim(':', '：', ' ');
                if (rest.Length > 0)
                {
                    radioName = rest;
                }
            }
        }

        var body = string.Join("\n", lines.Skip(1));
        if (body.Length == 0)
        {
            throw LlmException.ScriptParseFailed("お便りの本文が空です");
        }
        return new ListenerLetter(radioName, body);
    }

    /// <summary>行頭の装飾（箇条書き・強調・引用）と前後の強調記号を除去する（Mac と同じ明示文字集合）。</summary>
    private static string Strip(string rawLine)
    {
        var line = Whitespace.TrimHorizontal(rawLine);
        while (line.Length > 0 && "*-•>#".Contains(line[0]))
        {
            line = Whitespace.TrimHorizontal(line[1..]);
        }
        return line.Trim('*');
    }
}
