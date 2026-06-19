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
        string journalContext = "", string banterDirective = "", CancellationToken ct = default)
    {
        var request = MakeRequest(corner, djs, song, theme, dateContext, letter, greeting, guest, journalContext, banterDirective, _temperature);
        var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
        return Parse(raw, djs);
    }

    // MARK: - プロンプト構築（W6: free_talk / W12: お便り + 日付・季節 / W13.5: 冒頭挨拶 greeting / W14: ゲスト / W18: 長期記憶 journalContext）

    /// <param name="djs">出演者（**順序付き・先頭＝メイン**、ゲストコーナーは末尾にゲスト）。メインが主導し、他は相槌・ツッコミ・応答で返す（W13.5 §6）。</param>
    /// <param name="greeting">冒頭コーナーのみ非 null（時刻連動の挨拶語）。非 null＝挨拶＋出演者紹介、null＝挨拶抑制で即本題。</param>
    /// <param name="guest">ゲストコーナーのみ非 null（迎えるゲスト）。専門家フレーミング制約を付与（W14 §4）。</param>
    /// <param name="journalContext">冒頭コーナーのみ非空（前回までの当週ハイライト振り返り。W18 §4。dateContext と同型の注入経路）。</param>
    /// <param name="banterDirective">掛け合い指示（W-DLG）。free_talk のみ非空（当日キャストの掛け合いルール指示文）。非空なら制約として注入する。</param>
    public static LLMRequest MakeRequest(
        CornerTemplate corner, IReadOnlyList<DjProfile> djs, TrackInfo song, string? theme = null,
        string dateContext = "", ListenerLetter? letter = null, string? greeting = null, DjProfile? guest = null,
        string journalContext = "", string banterDirective = "", double temperature = 0.9)
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
        // 前回までの振り返り（長期記憶。冒頭コーナーのみ＝BroadcastEngine が opening の context にだけ設定。W18 §6）。
        if (journalContext.Length > 0)
        {
            sections.Add($"# 前回までの番組の振り返り\n{journalContext}");
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
            // 標準13項目の③「DJの今日の気分」（W16 §3-1 / Mac s16 ①）。冒頭の会話のフリとして本題へ橋渡しする。
            constraints.Add($"出演者紹介のあと、メイン「{main}」が『今日の気分』を一言（調子・気分や、今日の天気・季節の感想など）添えてから、本題へ自然に橋渡しする。");
            // 時刻・時間帯の断定抑制（W16 §3-2。Mac shipped の post-spec ライブ修正）。気分・天気を語ると時間帯（time-of-day）を口走りやすく実時刻とズレるため、挨拶語にとどめる。
            constraints.Add($"挨拶は「{greeting}」のような挨拶語にとどめ、『深夜』『夕方』『◯時』など具体的な時刻・時間帯は断定しない（正確な時刻はこの場面では言わない）。");
            // 長期記憶（W18 §6）。前回の振り返りがあれば軽く一言だけ触れる（長々と振り返らない）。冒頭コーナー限定。
            if (journalContext.Length > 0)
            {
                constraints.Add("冒頭で、前回までの放送の振り返り（上記）に軽く一言だけ触れてから本題へ入る（長々と振り返らない）。");
            }
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
        // 掛け合い指示（W-DLG）。config 由来の指示文をそのまま 1 制約として追加（free_talk のみ・engine 側で範囲限定済み）。
        if (!string.IsNullOrEmpty(banterDirective))
        {
            constraints.Add(banterDirective);
        }
        constraints.Add(songInstruction);
        sections.Add("# 制約\n" + string.Join("\n", constraints.Select(c => $"- {c}")));

        var system =
            "あなたはラジオ番組「ケイラボAIラジオ」の放送作家です。\n" +
            "リスナーが作業しながら聴ける、肩の力の抜けた楽しい会話を書きます。\n\n" +
            "# 出演DJ\n" + profiles;

        return new LLMRequest(string.Join("\n\n", sections), System: system, Temperature: temperature);
    }

    /// <summary>
    /// アーティスト特集（W15）の発話パート別 LLM リクエスト。各パートを個別生成する（<see cref="MakeRequest"/> とは別の兄弟ビルダー）。
    /// 曲名は与えられた表記をそのまま使わせる（プレフライト済みの実曲名と一致＝紹介と再生の不一致を防ぐ）。
    /// 途中コーナーの anti-show-close 句は <see cref="MakeRequest"/> の途中分岐と同一の Win 形を使う（spec §18-7）。
    /// </summary>
    public static LLMRequest MakeArtistFeatureRequest(
        ArtistFeaturePart part, string artistName, IReadOnlyList<DjProfile> djs,
        string dateContext = "", int targetCharacters = 0, double temperature = 0.9)
    {
        var names = string.Join("」「", djs.Select(d => d.Name));
        var main = djs.Count > 0 ? djs[0].Name : names;
        var profiles = string.Join("\n", djs.Select(d => $"- {d.Name}: {d.Persona}"));

        var constraints = new List<string>
        {
            $"セリフの合計は {targetCharacters} 文字以上、{targetCharacters * 12 / 10} 文字以内。短すぎる台本は不可。",
            "出力は台本のみ。1 行につき 1 つのセリフを「DJ名: セリフ」の形式で書く。",
            $"DJ名は「{names}」のみ。ナレーション、ト書き、見出し、記号装飾は書かない。",
            $"進行はメイン「{main}」が主導し、ほかの出演者は相槌・ツッコミ・応答で自然に返す。",
            "各 DJ は上記プロフィールにある自分の一人称・口調・語尾だけを使う。ある DJ の特徴的な語尾（例:「〜のだ」）を、その DJ 以外のセリフに混ぜてはいけない（とくにメインの語尾を他の出演者に伝染させない）。",
            "これは番組の途中。挨拶・自己紹介・番組名の名乗りはせず、いきなり本題から始める。このあとも別のコーナーが続くので、番組全体を締めくくる言い方（「本日最後の曲」「ラストナンバー」「また来週」「お別れの時間」など）はしない。",
        };

        List<string> sections;
        switch (part)
        {
            case ArtistFeaturePart.Intro:
                sections = new List<string> { "ラジオ番組のアーティスト特集の「導入」の会話台本を書いてください。" };
                constraints.Add($"メイン「{main}」が『ここからはアーティスト特集』と宣言し、本日特集するアーティスト「{artistName}」への思いや好きなところを一言添える。");
                constraints.Add("まだ曲名には触れない（曲の紹介は次のパートで行う）。");
                break;
            case ArtistFeaturePart.GroupIntro gi:
                var isFirst = gi.Index == 0;
                var isLast = gi.Index == gi.Total - 1;
                var list = string.Join("、", gi.Tracks.Select(t => $"「{t.Title}」（{t.Artist}）"));
                sections = new List<string>
                {
                    $"ラジオ番組のアーティスト特集で、これから連続で流す {gi.Tracks.Count} 曲をまとめて紹介する会話台本を書いてください。",
                    $"# 紹介する曲（この順で）\n{list}",
                };
                constraints.Add("上の曲を順に紹介する。曲名・アーティスト名は与えられた表記を一字一句そのまま言い、言い換え・推測・別情報の捏造をしない。");
                if (isFirst)
                {
                    // 1 回目（または唯一）のグループ: 従来どおり自然に曲紹介へ入る（仕様 w15 §7）。
                    constraints.Add("各曲に聴きどころを軽く添え、曲へ送り出して締める。");
                }
                else
                {
                    // 2 回目以降のグループ: すでに進行中の特集を「続ける」つなぎにする。
                    // 「続いては〇〇特集」のように新しく特集が始まる言い方は禁止（ライブ確認 fix2、仕様 w15 §7）。
                    constraints.Add($"この特集は「{artistName}」特集としてすでに進行中で、前のグループを聴き終えたところ。「{artistName}特集です」と新しく始めるような言い方（「続いては{artistName}特集」など）は禁止。「引き続き{artistName}の曲を」「同じ{artistName}から、次は」のように、いま進行中の特集をそのまま続けるつなぎで前を受ける。アーティストの紹介はやり直さず、曲の紹介に集中する。");
                    constraints.Add("各曲に聴きどころを軽く添え、曲へ送り出して締める。");
                }
                if (isLast && !isFirst)
                {
                    // 最後のグループ: 曲数に応じてラストである旨を一言添える（縮約で 1〜3 曲、仕様 w15 §7）。
                    constraints.Add($"これが特集で流す最後のグループ。「最後はこの {gi.Tracks.Count} 曲」のように、曲数（{gi.Tracks.Count} 曲）に応じて最後である旨を一言添える。");
                }
                break;
            case ArtistFeaturePart.Comment comment:
                sections = new List<string> { "ラジオ番組のアーティスト特集で、今流した曲を聴いたあとの感想・雑談の会話台本を書いてください。" };
                constraints.Add(comment.Shorter
                    ? "前の感想より短く、テンポよくまとめる。"
                    : "出演者で曲やアーティストの感想を自由に話す（少し脱線してよい）。");
                constraints.Add("次の曲紹介には踏み込まない（紹介は別パートで行う）。");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part));
        }

        if (dateContext.Length > 0)
        {
            sections.Add($"# 今日の日付と季節\n{dateContext}");
            constraints.Add("季節や時候の話に触れるなら、上の日付・季節に合わせる。");
        }
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
