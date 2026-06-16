using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class DialogueScriptGeneratorTests
{
    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    [Fact]
    public void Parse_MapsDjNamesToLines_StripsDecoration_AllowsBothColons()
    {
        const string raw =
            "ずんだもん: こんにちは、なのだ。\n" +
            "**四国めたん**：どうも。\n" +              // 強調 + 全角コロン
            "- ずんだもん: 今日は音楽の話なのだ。\n" +   // 箇条書き装飾
            "ナレーション: これは無視される\n" +        // 未登録 DJ 名 → 無視
            "四国めたん: いいですわね。";

        var script = DialogueScriptGenerator.Parse(raw, Djs);

        Assert.Equal(4, script.Lines.Count);
        Assert.Equal(new DialogueLine("zundamon", "こんにちは、なのだ。"), script.Lines[0]);
        Assert.Equal(new DialogueLine("metan", "どうも。"), script.Lines[1]);
        Assert.Equal(new DialogueLine("zundamon", "今日は音楽の話なのだ。"), script.Lines[2]);
        Assert.Equal("metan", script.Lines[3].DjId);
    }

    [Fact]
    public void Parse_TooFewLines_ThrowsScriptParseFailed()
    {
        const string raw = "ずんだもん: 一行だけなのだ。";

        var ex = Assert.Throws<LlmException>(() => DialogueScriptGenerator.Parse(raw, Djs));
        Assert.Equal("E-LLM-SCRIPT-PARSE-FAILED-001", ex.Code);
    }

    [Fact]
    public void MakeRequest_IncludesThemeNamesAndConstraints()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("音楽", request.Prompt);              // テーマ
        Assert.Contains("ずんだもん", request.Prompt);        // DJ 名
        Assert.Contains("DJ名: セリフ", request.Prompt);      // 出力契約
        Assert.Contains("アイドル", request.Prompt);          // 確定曲の紹介指示
        Assert.NotNull(request.System);
        Assert.Contains("放送作家", request.System!);          // system プロンプト
        Assert.DoesNotContain("# 今日の日付と季節", request.Prompt); // dateContext 未指定なら季節節なし
        Assert.Contains("# テーマ", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithDateContext_InjectsSeasonSectionAndConstraint()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(
            corner, Djs, song, theme: "音楽", dateContext: "今日は6月12日、梅雨の時期です。");

        Assert.Contains("# 今日の日付と季節", request.Prompt);
        Assert.Contains("今日は6月12日、梅雨の時期です。", request.Prompt);
        Assert.Contains("季節や時候の話は、上の日付・季節に合わせる。", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithLetter_ReplacesThemeWithLetter_AndAddsRequestSongIntro()
    {
        var corner = new CornerTemplate(
            "letter", "お便り", "音楽", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");
        var letter = new ListenerLetter("梅雨好き", "雨の日が好きです。");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽", letter: letter);

        Assert.Contains("# リスナーからのお便り", request.Prompt);
        Assert.Contains("ラジオネーム: 梅雨好き", request.Prompt);
        Assert.Contains("雨の日が好きです。", request.Prompt);
        Assert.DoesNotContain("# テーマ", request.Prompt);               // letter ありはテーマ節を出さない
        Assert.Contains("梅雨好きさんからのリクエスト曲", request.Prompt); // 曲振りの letter 変種
        Assert.DoesNotContain("テーマの余韻から自然に", request.Prompt);  // free_talk 変種は混ざらない
    }

    [Fact]
    public void MakeRequest_FreeTalk_HasNoLetterArtifacts()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("テーマの余韻から自然に", request.Prompt); // free_talk の曲振り
        Assert.DoesNotContain("リクエスト曲", request.Prompt);     // letter 変種は混ざらない
        Assert.DoesNotContain("お便り", request.Prompt);
        Assert.DoesNotContain("ラジオネーム", request.Prompt);
    }

    [Fact]
    public void MakeRequest_MidProgramCorner_ForbidsProgramEndingPhrases()
    {
        // コーナーは番組途中。番組全体を締めくくる言い方（「ラストナンバー」「また来週」等）を抑止し、
        // 締めは「コーナーを締める」単位であることを明示する（実機で free_talk が番組終了風に話した事象の対策）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("番組全体を締めくくる言い方", request.Prompt);
        Assert.Contains("ラストナンバー", request.Prompt); // 禁止例が明示されている
        Assert.Contains("コーナーを締める", request.Prompt); // 締めはコーナー単位
    }
}
