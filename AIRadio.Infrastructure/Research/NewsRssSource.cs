using System.Text;
using System.Xml;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// Google News RSS から最新ニュース見出しを取得する <see cref="IResearchSource"/>。
/// Mac 版 `NewsRssSource`（XMLParser SAX）を <see cref="XmlReader"/> ストリーミングで移植。
/// </summary>
public sealed class NewsRssSource : IResearchSource
{
    private const string DefaultRssUrl = "https://news.google.com/rss?hl=ja&gl=JP&ceid=JP:ja";

    private readonly Uri _url;
    private readonly int _maxItems;
    private readonly IHttpClient _http;

    public NewsRssSource(string url, IHttpClient http, int maxItems = 5)
    {
        _url = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : new Uri(DefaultRssUrl);
        _maxItems = maxItems;
        _http = http;
    }

    public async Task<string> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _http.GetAsync(_url, null, ct).ConfigureAwait(false);
            var titles = ParseItemTitles(data, _maxItems)
                .Select(CleanTitle)
                .Where(t => t.Length > 0)
                .ToList();
            if (titles.Count == 0)
            {
                throw ResearchException.NewsFetchFailed("ニュース見出しが取得できませんでした");
            }
            return string.Join("。", titles) + "。";
        }
        catch (ResearchException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ResearchException.NewsFetchFailed(ex.Message);
        }
    }

    /// <summary>`見出し - メディア名` の末尾メディア名を除去する（見出し内の ` - ` は保持）。</summary>
    public static string CleanTitle(string title)
    {
        var parts = title.Split(" - ");
        var headline = parts.Length > 1 ? string.Join(" - ", parts[..^1]) : title;
        return headline.Trim();
    }

    /// <summary>RSS の `&lt;item&gt;&lt;title&gt;` だけを集める（channel タイトルは除外、CDATA 対応）。</summary>
    private static List<string> ParseItemTitles(byte[] data, int limit)
    {
        var titles = new List<string>();
        using var stream = new MemoryStream(data);
        // 名前空間を解釈しない（Mac の XMLParser.shouldProcessNamespaces=false 相当）。これにより
        // LocalName が修飾名（例 "media:title"）のままになり、== "title" 比較で名前空間付き要素
        // （media:title / dc:title 等。Google News RSS に実在）を確実に除外できる。未宣言 prefix にも頑健。
        // （XmlReaderSettings には Namespaces 設定が無いため、この用途では XmlTextReader を使う。）
        using var reader = new XmlTextReader(stream)
        {
            Namespaces = false,
            DtdProcessing = DtdProcessing.Ignore,
            WhitespaceHandling = WhitespaceHandling.None,
        };

        var inItem = false;
        var inTitle = false;
        var buffer = new StringBuilder();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "item")
                    {
                        inItem = true;
                    }
                    else if (reader.LocalName == "title" && inItem)
                    {
                        inTitle = true;
                        buffer.Clear();
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (inTitle)
                    {
                        buffer.Append(reader.Value);
                    }
                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == "title" && inTitle)
                    {
                        inTitle = false;
                        titles.Add(buffer.ToString().Trim());
                    }
                    else if (reader.LocalName == "item")
                    {
                        inItem = false;
                    }
                    break;
            }
        }
        return titles.Take(limit).ToList();
    }
}
