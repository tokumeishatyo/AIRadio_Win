using System.Text;

namespace AIRadio.Core;

/// <summary>
/// テーマ + DJ ペルソナ + 確定済みの締め曲から会話台本を LLM 生成する。Mac 版 `DialogueScriptGenerator` の移植（W6 は free_talk）。
/// 出力契約: 1 行 = `DJ名: セリフ`。登録 DJ 名で始まらない行は無視（マークダウン混入対策）。
/// </summary>
public sealed class DialogueScriptGenerator
{
    private readonly ILLMBackend _llm;
    private readonly double _temperature;

    public DialogueScriptGenerator(ILLMBackend llm, double temperature = 0.9)
    {
        _llm = llm;
        _temperature = temperature;
    }

    public async Task<DialogueScript> GenerateAsync(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, TrackInfo song, string? theme = null,
        CancellationToken ct = default)
    {
        var request = MakeRequest(corner, djs, song, theme, _temperature);
        var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
        return Parse(raw, djs);
    }

    // MARK: - プロンプト構築（W6: free_talk。挨拶/お便り/ゲスト/日付/長期記憶は後続スライスで追加）

    public static LLMRequest MakeRequest(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, TrackInfo song, string? theme = null,
        double temperature = 0.9)
    {
        var selectedTheme = theme ?? corner.Theme;
        var names = string.Join("」「", djs.Select(d => d.Name));
        var main = djs.Count > 0 ? djs[0].Name : names;
        var profiles = string.Join("\n", djs.Select(d => $"- {d.Name}: {d.Persona}"));

        // フォールバック曲はタイトル不明（URI のみ）のことがある。その場合は曲名を捏造させず匿名で紹介させる。
        var songIntro = song.Title.Length == 0
            ? "曲名を明かさず「この後の一曲」として曲振りして締める。曲名やアーティスト名を推測して言ってはいけない。"
            : $"「{song.Artist}」の「{song.Title}」を紹介して締める。";
        var songInstruction = $"コーナーの最後は、テーマの余韻から自然に{songIntro}";

        var sections = new List<string>
        {
            $"ラジオコーナー「{corner.Title}」の会話台本を書いてください。",
            $"# テーマ\n{selectedTheme}",
        };

        var constraints = new List<string>
        {
            $"セリフの合計は {corner.TargetCharacters} 文字以上、{corner.TargetCharacters * 12 / 10} 文字以内（{corner.TargetMinutes} 分程度の会話）。短すぎる台本は不可。",
            "出力は台本のみ。1 行につき 1 つのセリフを「DJ名: セリフ」の形式で書く。",
            $"DJ名は「{names}」のみ。ナレーション、ト書き、見出し、記号装飾は書かない。",
            $"進行はメイン「{main}」が主導し、ほかの出演者は相槌・ツッコミ・応答で自然に返す。",
            "各 DJ は上記プロフィールにある自分の一人称・口調・語尾だけを使う。ある DJ の特徴的な語尾（例:「〜のだ」）を、その DJ 以外のセリフに混ぜてはいけない（とくにメインの語尾を他の出演者に伝染させない）。",
            "これは番組の途中のコーナー。挨拶・自己紹介・番組名の名乗りはせず、いきなり本題から始める。",
            songInstruction,
        };
        sections.Add("# 制約\n" + string.Join("\n", constraints.Select(c => $"- {c}")));

        var system =
            "あなたはラジオ番組「ケイラボAIラジオ」の放送作家です。\n" +
            "リスナーが作業しながら聴ける、肩の力の抜けた楽しい会話を書きます。\n\n" +
            "# 出演DJ\n" + profiles;

        return new LLMRequest(string.Join("\n\n", sections), System: system, Temperature: temperature);
    }

    // MARK: - パース

    public static DialogueScript Parse(string raw, IReadOnlyList<DjProfile> djs, int minLines = 4)
    {
        var lines = new List<DialogueLine>();
        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = ParseLine(rawLine, djs);
            if (line is not null)
            {
                lines.Add(line);
            }
        }
        if (lines.Count < minLines)
        {
            throw LlmException.ScriptParseFailed($"セリフが {lines.Count} 行しか取れませんでした（最低 {minLines} 行）");
        }
        return new DialogueScript(lines);
    }

    private static DialogueLine? ParseLine(string rawLine, IReadOnlyList<DjProfile> djs)
    {
        // 行頭の装飾（箇条書き・強調・引用）を除去してから DJ 名を照合する。
        var line = Whitespace.TrimHorizontal(rawLine);
        while (line.Length > 0 && "*-•>#".Contains(line[0]))
        {
            line = Whitespace.TrimHorizontal(line[1..]);
        }
        foreach (var dj in djs)
        {
            if (!line.StartsWith(dj.Name, StringComparison.Ordinal))
            {
                continue;
            }
            // 「名前**: セリフ」のような強調の残りも許容する（Mac と同じ明示文字集合）。
            var rest = line[dj.Name.Length..].Trim('*', ' ');
            if (rest.Length == 0 || (rest[0] != ':' && rest[0] != '：'))
            {
                continue;
            }
            var text = Whitespace.TrimHorizontal(rest[1..]);
            return text.Length == 0 ? null : new DialogueLine(dj.Id, text);
        }
        return null;
    }
}
