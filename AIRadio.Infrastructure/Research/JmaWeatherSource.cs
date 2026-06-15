using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>気象庁 予報 API から当日の天気を取得する <see cref="IResearchSource"/>。Mac 版 `JmaWeatherSource` の移植。</summary>
public sealed class JmaWeatherSource : IResearchSource
{
    private readonly string _areaCode;
    private readonly string _areaName;
    private readonly IHttpClient _http;

    public JmaWeatherSource(string areaCode, string areaName, IHttpClient http)
    {
        _areaCode = areaCode;
        _areaName = areaName;
        _http = http;
    }

    public async Task<string> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var url = new Uri($"https://www.jma.go.jp/bosai/forecast/data/forecast/{_areaCode}.json");
            var data = await _http.GetAsync(url, null, ct).ConfigureAwait(false);
            var forecasts = JsonSerializer.Deserialize<List<Forecast>>(data);
            var weather = ExtractWeather(forecasts, _areaName);
            if (weather is null)
            {
                throw ResearchException.WeatherFetchFailed("天気予報が取得できませんでした");
            }
            return $"{_areaName}は、{weather}、という予報です。";
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
            throw ResearchException.WeatherFetchFailed(ex.Message);
        }
    }

    internal static string? ExtractWeather(List<Forecast>? forecasts, string areaName)
    {
        var first = forecasts?.FirstOrDefault();
        if (first?.TimeSeries is null)
        {
            return null;
        }
        foreach (var series in first.TimeSeries)
        {
            var areas = series.Areas ?? new List<Forecast.Area>();
            var area = areas.FirstOrDefault(a => a.AreaInfo?.Name == areaName && a.Weathers is { Count: > 0 })
                ?? areas.FirstOrDefault(a => a.Weathers is { Count: > 0 });
            var weather = area?.Weathers?.FirstOrDefault();
            if (weather is not null)
            {
                return Normalize(weather);
            }
        }
        return null;
    }

    /// <summary>気象庁の天気文字列は全角空白（U+3000）で区切られる（例: `くもり　夕方　から　雨`）。読み上げ用に除去する。</summary>
    public static string Normalize(string weather) => weather.Replace("　", "");

    internal sealed class Forecast
    {
        [JsonPropertyName("timeSeries")] public List<TimeSeriesEntry>? TimeSeries { get; set; }

        internal sealed class TimeSeriesEntry
        {
            [JsonPropertyName("areas")] public List<Area>? Areas { get; set; }
        }

        internal sealed class Area
        {
            [JsonPropertyName("area")] public AreaInfoEntry? AreaInfo { get; set; }
            [JsonPropertyName("weathers")] public List<string>? Weathers { get; set; }
        }

        internal sealed class AreaInfoEntry
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
        }
    }
}
