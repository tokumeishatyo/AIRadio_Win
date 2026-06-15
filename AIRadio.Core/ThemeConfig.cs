namespace AIRadio.Core;

/// <summary>
/// 統一テーマ/BGM 演出の設定（OP / ニュース / ED が共有）。
/// 演出ロジック（<see cref="ThemeSequencer"/>）に渡す値オブジェクト。
/// </summary>
/// <param name="Tagline">BGM 前に喋る一言（ED は null = いきなり BGM）。</param>
/// <param name="TrackUri">BGM の Spotify トラック URI（<c>spotify:track:&lt;ID&gt;</c> に正規化済み）。</param>
/// <param name="IntroSeconds">ダッキング前にフル音量で流す秒数。</param>
/// <param name="Volume">フル音量 %（0-100）。</param>
/// <param name="DuckedVolume">発話中の音量 %（0-100）。</param>
/// <param name="OutroSeconds">
/// 発話後、曲の「残り OutroSeconds 秒」へシークし、曲の自然な終わりで停止する
/// （シーク位置 = 曲の長さ - OutroSeconds。曲の長さが不明な場合はシークせずそのまま）。
/// </param>
public sealed record ThemeConfig(
    string? Tagline,
    string TrackUri,
    int IntroSeconds,
    int Volume,
    int DuckedVolume,
    int OutroSeconds);
