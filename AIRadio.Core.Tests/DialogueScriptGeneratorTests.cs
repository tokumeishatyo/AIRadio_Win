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
    }
}
