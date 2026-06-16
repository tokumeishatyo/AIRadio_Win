using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W11 <see cref="LlmNewsScriptProvider"/>: 素材取得 → LLM 本文 → 固定イントロ/アウトロ組み立て、
/// LLM 失敗時の定型テンプレ自己完結フォールバック、素材失敗のフォールバック文言、停止（OCE）の伝播（§3-1）。
/// </summary>
public class LlmNewsScriptProviderTests
{
    private static NewsScriptStyle Style(string intro = "イントロ。", string outro = "アウトロ。", string styleHint = "")
        => new(StyleHint: styleHint, Intro: intro, Outro: outro);

    [Fact]
    public async Task Announcement_AssemblesIntroBodyOutro_FromSanitizedLlmBody()
    {
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch("見出し。"),
            weather: new StubResearch("晴れ。"),
            llm: new StubLlm("# 本文\n- LLM の本文。"),
            persona: "キャスター",
            style: Style(intro: "時刻は{hour12}時{minute}分。", outro: "以上です。"),
            fallbackTemplate: "FALLBACK {news} {weather}");

        var text = await provider.AnnouncementAsync();

        // {intro} {body} {outro}。本文は装飾除去済み。{hour12}/{minute} は未展開のまま（二段展開はエンジン）。
        Assert.Equal("時刻は{hour12}時{minute}分。 本文 LLM の本文。 以上です。", text);
    }

    [Fact]
    public async Task Announcement_FallsBackToTemplate_OnLlmFailure()
    {
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch("ニュース本文。"),
            weather: new StubResearch("天気本文。"),
            llm: new StubLlm(LlmException.ApiFailed("HTTP 503")),
            persona: "p",
            style: Style(),
            fallbackTemplate: "定型: {news} / {weather}");

        var text = await provider.AnnouncementAsync();

        Assert.Equal("定型: ニュース本文。 / 天気本文。", text); // fail-tolerant（放送は止めない）
    }

    [Fact]
    public async Task Announcement_FallsBackToTemplate_OnEmptyLlmBody()
    {
        // LLM 応答が装飾のみ → Sanitize が EmptyResponse → フォールバックに倒れる。
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch("N。"),
            weather: new StubResearch("W。"),
            llm: new StubLlm("##\n**"),
            persona: "p",
            style: Style(),
            fallbackTemplate: "定型 {news}|{weather}");

        var text = await provider.AnnouncementAsync();

        Assert.Equal("定型 N。|W。", text);
    }

    [Fact]
    public async Task Announcement_UsesFetchFallbacks_AndStillGeneratesBody()
    {
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch(ResearchException.NewsFetchFailed("x")),
            weather: new StubResearch(ResearchException.WeatherFetchFailed("y")),
            llm: new EchoLlm(), // プロンプトをそのまま本文化（フォールバック素材がプロンプトに乗ることを確認）
            persona: "p",
            style: Style(intro: "I", outro: "O"),
            fallbackTemplate: "F {news} {weather}",
            newsFallback: "NEWS_NG",
            weatherFallback: "WX_NG");

        var text = await provider.AnnouncementAsync();

        Assert.StartsWith("I ", text);
        Assert.EndsWith(" O", text);
        Assert.Contains("NEWS_NG", text); // フォールバック素材が本文に乗っている
        Assert.Contains("WX_NG", text);
    }

    [Fact]
    public async Task Announcement_PropagatesCancellation_FromFetch_NotFallback()
    {
        // 停止（キャンセル）はフォールバックに握り潰さず伝播させる（完全静寂 §3-1）。
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch(new OperationCanceledException()),
            weather: new StubResearch("天気。"),
            llm: new StubLlm("本文。"),
            persona: "p",
            style: Style(),
            fallbackTemplate: "F {news} {weather}");

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.AnnouncementAsync());
    }

    [Fact]
    public async Task Announcement_PropagatesCancellation_FromLlm_NotFallback()
    {
        var provider = new LlmNewsScriptProvider(
            news: new StubResearch("見出し。"),
            weather: new StubResearch("晴れ。"),
            llm: new StubLlm(new OperationCanceledException()),
            persona: "p",
            style: Style(),
            fallbackTemplate: "F {news} {weather}");

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.AnnouncementAsync());
    }

    // --- fakes（NewsWeatherProviderTests と同様、テスト内で自己完結） ---

    private sealed class StubResearch : IResearchSource
    {
        private readonly string? _result;
        private readonly Exception? _error;

        public StubResearch(string result) => _result = result;
        public StubResearch(Exception error) => _error = error;

        public Task<string> FetchAsync(CancellationToken ct = default)
            => _error is not null ? Task.FromException<string>(_error) : Task.FromResult(_result!);
    }

    private sealed class StubLlm : ILLMBackend
    {
        private readonly string? _result;
        private readonly Exception? _error;

        public StubLlm(string result) => _result = result;
        public StubLlm(Exception error) => _error = error;

        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
            => _error is not null ? Task.FromException<string>(_error) : Task.FromResult(_result!);
    }

    private sealed class EchoLlm : ILLMBackend
    {
        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
            => Task.FromResult(request.Prompt);
    }
}
