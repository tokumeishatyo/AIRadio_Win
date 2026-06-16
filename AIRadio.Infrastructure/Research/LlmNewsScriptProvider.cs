using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// ニュース原稿の生成（W11）: 素材取得 → LLM でアナウンサー本文を生成 → 固定イントロ/アウトロと組み立てる。
/// fail-tolerant: 素材の個別取得失敗はフォールバック文言、**LLM 失敗時は定型テンプレ原稿に倒して放送継続**
/// （Mac <c>LlmNewsScriptProvider</c> 同様、<see cref="NewsWeatherProvider"/> には依存せず自己完結でテンプレ展開）。
/// 停止（<see cref="OperationCanceledException"/>）はフォールバックに握り潰さず**伝播**する（完全静寂 §3-1）。
///
/// <para>時報イントロ・締めのアウトロに含まれる <c>{hour12}</c>/<c>{minute}</c> は本クラスでは展開せず原文保持し、
/// <c>BroadcastEngine</c> が発話直前に展開する（W8 二段展開）。</para>
///
/// <see cref="NewsWeatherProvider"/> と同じ <c>Task&lt;string&gt; AnnouncementAsync(CancellationToken)</c> 形を持ち、
/// エンジンのニュース seam（デリゲート、§3-4）へそのまま差し込める。
/// </summary>
public sealed class LlmNewsScriptProvider
{
    private readonly IResearchSource _news;
    private readonly IResearchSource _weather;
    private readonly ILLMBackend _llm;
    private readonly string _persona;
    private readonly NewsScriptStyle _style;
    /// <summary>LLM 失敗時の定型原稿（<c>{news}</c>/<c>{weather}</c> を展開、W5 の announcement_template）。</summary>
    private readonly string _fallbackTemplate;
    private readonly string _newsFallback;
    private readonly string _weatherFallback;

    public LlmNewsScriptProvider(
        IResearchSource news,
        IResearchSource weather,
        ILLMBackend llm,
        string persona,
        NewsScriptStyle style,
        string fallbackTemplate,
        string newsFallback = "本日のニュースは準備中です。",
        string weatherFallback = "天気予報は準備中です。")
    {
        _news = news;
        _weather = weather;
        _llm = llm;
        _persona = persona;
        _style = style;
        _fallbackTemplate = fallbackTemplate;
        _newsFallback = newsFallback;
        _weatherFallback = weatherFallback;
    }

    public async Task<string> AnnouncementAsync(CancellationToken ct = default)
    {
        var newsText = await TryFetchAsync(_news, _newsFallback, ct).ConfigureAwait(false);
        var weatherText = await TryFetchAsync(_weather, _weatherFallback, ct).ConfigureAwait(false);
        try
        {
            var request = NewsScriptGenerator.MakeRequest(
                newsText, weatherText, _persona, _style.TargetCharacters, _style.StyleHint);
            var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
            var body = NewsScriptGenerator.Sanitize(raw);
            return $"{_style.Intro} {body} {_style.Outro}";
        }
        catch (OperationCanceledException)
        {
            throw; // 完全静寂（§3-1）: 停止はフォールバックに握り潰さず伝播。
        }
        catch
        {
            // LLM 不調（API 失敗・空応答等）でも放送は止めない（定型テンプレ原稿に倒す）。
            return TemplateExpander.Expand(
                _fallbackTemplate,
                new Dictionary<string, string> { ["news"] = newsText, ["weather"] = weatherText });
        }
    }

    private static async Task<string> TryFetchAsync(IResearchSource source, string fallback, CancellationToken ct)
    {
        try
        {
            return await source.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 停止は伝播（完全静寂の後始末は呼び出し側）。
        }
        catch
        {
            return fallback; // 取得失敗は握り潰してフォールバック（fail-tolerant）。
        }
    }
}
