using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// ニュースと天気を取得し、テンプレートに展開してニュース原稿を生成する（**fail-tolerant**）。Mac 版 `NewsWeatherProvider` の移植。
/// 取得失敗はフォールバック文言で握り潰す（放送事故ゼロ系、§4-3）。停止（キャンセル）だけは伝播させる。
/// 実差し替え境界ではない（design §2 に `AnnouncementProviding` を含めない）ため具象クラス（§3-5）。
/// </summary>
public sealed class NewsWeatherProvider
{
    private readonly IResearchSource _news;
    private readonly IResearchSource _weather;
    private readonly string _template;
    private readonly string _newsFallback;
    private readonly string _weatherFallback;

    public NewsWeatherProvider(
        IResearchSource news,
        IResearchSource weather,
        string template,
        string newsFallback = "本日のニュースは準備中です。",
        string weatherFallback = "天気予報は準備中です。")
    {
        _news = news;
        _weather = weather;
        _template = template;
        _newsFallback = newsFallback;
        _weatherFallback = weatherFallback;
    }

    /// <summary>`{news}` / `{weather}` を実データで展開した原稿を返す。取得失敗時はフォールバック文言。</summary>
    public async Task<string> AnnouncementAsync(CancellationToken ct = default)
    {
        var newsText = await TryFetchAsync(_news, _newsFallback, ct).ConfigureAwait(false);
        var weatherText = await TryFetchAsync(_weather, _weatherFallback, ct).ConfigureAwait(false);
        return TemplateExpander.Expand(
            _template,
            new Dictionary<string, string> { ["news"] = newsText, ["weather"] = weatherText });
    }

    private static async Task<string> TryFetchAsync(IResearchSource source, string fallback, CancellationToken ct)
    {
        try
        {
            return await source.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 停止は伝播（完全静寂の後始末は呼び出し側）
        }
        catch
        {
            return fallback; // 取得失敗は握り潰してフォールバック（fail-tolerant）
        }
    }
}
