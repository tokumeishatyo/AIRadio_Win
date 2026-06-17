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
        string dateContext = "", ListenerLetter? letter = null, string? greeting = null, DjProfile? guest = null,
        CancellationToken ct = default)
    {
        var request = MakeRequest(corner, djs, song, theme, dateContext, letter, greeting, guest, _temperature);
        var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
        return Parse(raw, djs);
    }

    // MARK: - プロンプト構築（W6: free_talk / W12: お便り + 日付・季節 / W13.5: 冒頭挨拶 greeting / W14: ゲスト。長期記憶は後続スライス）

    /// <param name="djs">出演者（**順序付き・先頭＝メイン**、ゲストコーナーは末尾にゲスト）。メインが主導し、他は相槌・ツッコミ・応答で返す（W13.5 §6）。</param>
    /// <param name="greeting">冒頭コーナーのみ非 null（時刻連動の挨拶語）。非 null＝挨拶＋出演者紹介、null＝挨拶抑制で即本題。</param>
    /// <param name="guest">ゲストコーナーのみ非 null（迎えるゲスト）。専門家フレーミング制約を付与（W14 §4）。</param>
    public static LLMRequest MakeRequest(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, TrackInfo song, string? theme = null,
        string dateContext = "", ListenerLetter? letter = null, string? greeting = null, DjProfile? guest = null,
        double temperature = 0.9)
    {
        var selectedTheme = theme ?? corner.Theme;
        var names = string.Join("」「", djs.Select(d => d.Name));
        var main = djs.Count > 0 ? djs[0].Name : names;
        var profiles = string.Join("\n", djs.Select(d => $"- {d.Name}: {d.Persona}"));

        // フォールバック曲はタイトル不明（URI のみ）のことがある。その場合は曲名を捏造させず匿名で紹介させる。
        var songIntro = song.Title.Length == 0
            ? "曲名を明かさず「この後の一曲」として曲振りしてコーナーを締める。曲名やアーティスト名を推測して言ってはいけない。"
            : $"「{song.Artist}」の「{song.Title}」を紹介してコーナーを締める。";
        var songInstruction = letter is not null
            ? $"コーナーの最後は、{letter.RadioName}さんからのリクエスト曲として、{songIntro}"
            : $"コーナーの最後は、テーマの余韻から自然に{songIntro}";

        var sections = new List<string>
        {
            $"ラジオコーナー「{corner.Title}」の会話台本を書いてください。",
        };
        // お便りコーナー（W12）はテーマの代わりにお便り本文を渡す。それ以外はテーマ。
        sections.Add(letter is not null
            ? $"# リスナーからのお便り\nラジオネーム: {letter.RadioName}\n{letter.Body}"
            : $"# テーマ\n{selectedTheme}");
        if (dateContext.Length > 0)
        {
            sections.Add($"# 今日の日付と季節\n{dateContext}");
        }

        var constraints = new List<string>
        {
            $"セリフの合計は {corner.TargetCharacters} 文字以上、{corner.TargetCharacters * 12 / 10} 文字以内（{corner.TargetMinutes} 分程度の会話）。短すぎる台本は不可。",
            "出力は台本のみ。1 行につき 1 つのセリフを「DJ名: セリフ」の形式で書く。",
            $"DJ名は「{names}」のみ。ナレーション、ト書き、見出し、記号装飾は書かない。",
            $"進行はメイン「{main}」が主導し、ほかの出演者は相槌・ツッコミ・応答で自然に返す。",
            "各 DJ は上記プロフィールにある自分の一人称・口調・語尾だけを使う。ある DJ の特徴的な語尾（例:「〜のだ」）を、その DJ 以外のセリフに混ぜてはいけない（とくにメインの語尾を他の出演者に伝染させない）。",
        };
        // 冒頭コーナーのみ挨拶＋出演者紹介。それ以外は挨拶・自己紹介・番組名を抑制して即本題（W13.5 §4）。
        if (greeting is not null)
        {
            constraints.Add($"これは番組の最初のコーナー。メイン「{main}」がまず「{greeting}」とリスナーに挨拶し、番組名「ケイラボAIラジオ」と本日の出演者（「{names}」）を紹介してから本題に入る。");
        }
        else
        {
            constraints.Add("これは番組の途中のコーナー。挨拶・自己紹介・番組名の名乗りはせず、いきなり本題から始める。このあとも別のコーナーが続くので、番組全体を締めくくる言い方（「本日最後の曲」「ラストナンバー」「また来週」「お別れの時間」など）はしない。");
        }
        if (letter is not null)
        {
            constraints.Add($"メインがまず「ラジオネーム {letter.RadioName}さんからのお便り」と紹介し、本文をセリフとして自然に読み上げる。");
            constraints.Add($"読み上げのあと、出演者でお便りへの感想を話す（テーマ: {selectedTheme}。脱線してよい）。");
        }
        if (guest is not null)
        {
            // ゲストコーナー（W14）: 正式紹介はリード文が済ませている前提で、軽い挨拶から専門家トークへ（Mac s14 §4）。
            constraints.Add($"これはゲストを迎えるコーナー。ゲスト「{guest.Name}」が冒頭で軽く挨拶し（番組名の名乗りや大げさな自己紹介は不要）、メイン「{main}」の進行でテーマ「{selectedTheme}」を会話する。");
            constraints.Add($"ゲスト「{guest.Name}」は「{selectedTheme}」に詳しい専門家として、具体的なエピソードや豆知識を交えて語る。サブは相づち・質問で会話を回す。");
            constraints.Add($"コーナーの最後に、メインがゲスト「{guest.Name}」へお礼を述べてから曲を紹介する。");
        }
        if (dateContext.Length > 0)
        {
            constraints.Add("季節や時候の話は、上の日付・季節に合わせる。");
        }
        constraints.Add(songInstruction);
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
