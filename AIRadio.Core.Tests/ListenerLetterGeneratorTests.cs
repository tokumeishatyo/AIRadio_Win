using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>W12 お便り生成（<see cref="ListenerLetterGenerator"/>）のプロンプト構築とパース契約。</summary>
public class ListenerLetterGeneratorTests
{
    [Fact]
    public void MakeRequest_IncludesThemeDateAndConstraints()
    {
        var req = ListenerLetterGenerator.MakeRequest("旅行・おでかけ", "今日は6月12日、梅雨の時期です。");

        Assert.Contains("旅行・おでかけ", req.Prompt);
        Assert.Contains("今日は6月12日、梅雨の時期です。", req.Prompt);
        Assert.Contains("1 行目はラジオネーム", req.Prompt);
        Assert.Contains("200 文字以上、400 文字以内", req.Prompt);
        Assert.Equal(0.9, req.Temperature);
    }

    [Fact]
    public void Parse_FirstLineRadioName_RestIsBody()
    {
        var letter = ListenerLetterGenerator.Parse("ずんだ太郎\n最近旅行に行きました。\nとても楽しかったです。");

        Assert.Equal("ずんだ太郎", letter.RadioName);
        Assert.Equal("最近旅行に行きました。\nとても楽しかったです。", letter.Body);
    }

    [Fact]
    public void Parse_StripsRadioNameLabel_AndDecoration()
    {
        var letter = ListenerLetterGenerator.Parse("- **ラジオネーム: 梅雨好き**\n雨の音が好きです。");

        Assert.Equal("梅雨好き", letter.RadioName);  // 装飾 + 「ラジオネーム:」ラベル除去
        Assert.Equal("雨の音が好きです。", letter.Body);
    }

    [Fact]
    public void Parse_Empty_ThrowsScriptParseFailed()
    {
        var ex = Assert.Throws<LlmException>(() => ListenerLetterGenerator.Parse("\n   \n"));
        Assert.Equal("E-LLM-SCRIPT-PARSE-FAILED-001", ex.Code);
    }

    [Fact]
    public void Parse_RadioNameOnly_NoBody_ThrowsScriptParseFailed()
    {
        var ex = Assert.Throws<LlmException>(() => ListenerLetterGenerator.Parse("ずんだ太郎"));
        Assert.Equal("E-LLM-SCRIPT-PARSE-FAILED-001", ex.Code);
    }

    [Fact]
    public void Parse_Crlf_PreservesCarriageReturn_LikeMacWhitespaces()
    {
        // Strip は Whitespace.TrimHorizontal（Mac .whitespaces 相当）で \r を落とさない＝Mac 忠実挙動を固定。
        // 行分割は '\n' のみ、複数行本文は '\n' 連結。実 LLM 応答は LF のため実害なし。
        var letter = ListenerLetterGenerator.Parse("散歩好き\r\n今日は晴れ。\r\nいい天気。");

        Assert.Equal("散歩好き\r", letter.RadioName);
        Assert.Equal("今日は晴れ。\r\nいい天気。", letter.Body);
    }
}
