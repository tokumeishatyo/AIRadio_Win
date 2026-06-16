using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W11 ニュース原稿生成の純粋ロジック（<see cref="NewsScriptGenerator"/>）。Mac 版 <c>NewsScriptGenerator</c> 移植の
/// 忠実性を検証する（プロンプト構築の出力契約・長さ境界の整数除算・整形の装飾除去/空 throw）。
/// </summary>
public class NewsScriptGeneratorTests
{
    [Fact]
    public void MakeRequest_BuildsPrompt_WithMaterialsAndOutputContract()
    {
        var req = NewsScriptGenerator.MakeRequest(
            news: "見出しA\n見出しB", weather: "晴れ時々くもり", persona: "落ち着いたキャスター", targetCharacters: 640);

        // 素材とセクション見出し。
        Assert.Contains("見出しA", req.Prompt);
        Assert.Contains("晴れ時々くもり", req.Prompt);
        Assert.Contains("# 本日のニュース見出し", req.Prompt);
        Assert.Contains("# 天気予報", req.Prompt);
        // 出力契約（本文のみ・定型句禁止）。
        Assert.Contains("出力は読み上げる本文のみ", req.Prompt);
        Assert.Contains("「ニュースの時間です」「以上です」", req.Prompt);
        // 長さ境界: 上限 = 640 * 12 / 10 = 768。
        Assert.Contains("合計 640 文字以上、768 文字以内", req.Prompt);
        // system にペルソナ + キャスター宣言。
        Assert.Contains("落ち着いたキャスター", req.System);
        Assert.Contains("ケイラボAIラジオ", req.System);
        Assert.Contains("ニュースキャスター", req.System);
        // ニュース原稿の温度は既定 0.6（グローバル LLM 温度とは独立）。
        Assert.Equal(0.6, req.Temperature);
    }

    [Fact]
    public void MakeRequest_AppendsStyleHintLine_OnlyWhenPresent()
    {
        var withHint = NewsScriptGenerator.MakeRequest("n", "w", "p", 100, styleHint: "簡潔に");
        Assert.Contains("語りのスタイル: 簡潔に", withHint.Prompt);

        var noHint = NewsScriptGenerator.MakeRequest("n", "w", "p", 100);
        Assert.DoesNotContain("語りのスタイル", noHint.Prompt);
    }

    [Fact]
    public void MakeRequest_UpperBound_UsesIntegerDivision()
    {
        // 105 * 12 / 10 = 126（整数除算、Swift Int 演算と一致。浮動小数の 126.0 ではない）。
        var req = NewsScriptGenerator.MakeRequest("n", "w", "p", 105);
        Assert.Contains("合計 105 文字以上、126 文字以内", req.Prompt);
    }

    [Fact]
    public void Sanitize_StripsMarkdownDecoration_AndJoinsWithSpace()
    {
        var raw = "# 見出し\n- 本文ひとつめ。\n**強調**の本文。\n> 引用の本文。";

        var result = NewsScriptGenerator.Sanitize(raw);

        Assert.Equal("見出し 本文ひとつめ。 強調の本文。 引用の本文。", result);
    }

    [Fact]
    public void Sanitize_DropsBlankLines()
    {
        var raw = "本文A。\n\n   \n本文B。";

        var result = NewsScriptGenerator.Sanitize(raw);

        Assert.Equal("本文A。 本文B。", result);
    }

    [Fact]
    public void Sanitize_OnlyDecorationOrBlank_ThrowsEmptyResponse()
    {
        var ex = Assert.Throws<LlmException>(() => NewsScriptGenerator.Sanitize("\n  \n##\n**"));
        Assert.Equal("E-LLM-EMPTY-RESPONSE-001", ex.Code);
    }

    [Fact]
    public void Sanitize_PreservesCarriageReturn_LikeMacWhitespaces()
    {
        // CRLF 入力: '\n' で分割後、各行末の '\r' は TrimHorizontal の対象外（Mac .whitespaces と同一＝CR を落とさない）。
        // 現挙動を意図として固定する（将来 TrimHorizontal を TrimEnd 等へ変えると検知される）。
        var result = NewsScriptGenerator.Sanitize("本文A。\r\n本文B。\r\n");
        Assert.Equal("本文A。\r 本文B。\r", result);
    }
}
