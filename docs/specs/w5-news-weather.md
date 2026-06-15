# W5 — ニュース・天気（News RSS + 気象庁、fail-tolerant）

> 対応 Mac 版: `NewsRssSource.swift` / `JmaWeatherSource.swift` / `NewsWeatherProvider.swift` / `ResearchConfig.swift`。
> いずれも無料・APIキー不要。**fail-tolerant**（取得失敗は放送を止めずフォールバック文言で継続。CLAUDE.md §4-3 / design §5）。
> LLM によるニュース会話化（`LlmNewsScriptProvider` / `NewsScriptStyle`）は **W11**。本スライスは定型テンプレ原稿のみ。

## 1. 概要
ニュース見出し（Google News RSS）と当日天気（気象庁 予報 API）を取得し、テンプレート `{news}` / `{weather}` に展開して
1 本のニュース原稿を生成する。取得は `IResearchSource`（Core, W0 既定義）越し、原稿合成は `NewsWeatherProvider`（具象）。

## 2. スコープ
**in**:
- Core エラー `ResearchException`: `NewsFetchFailed`（`E-NEWS-FETCH-FAILED-001`）/ `WeatherFetchFailed`（`E-WX-FETCH-FAILED-001`）。
- `NewsRssSource : IResearchSource`（Infra）: `GET` RSS → `<item><title>` を抽出（`System.Xml.XmlReader` ストリーミング、channel タイトル除外、CDATA 対応）→ `CleanTitle`（末尾 ` - メディア名` を除去、見出し内 ` - ` は保持）→ 空除去 → `。` 連結。`maxItems` 件まで。0 件は `NewsFetchFailed`。
- `JmaWeatherSource : IResearchSource`（Infra）: `GET https://www.jma.go.jp/bosai/forecast/data/forecast/{areaCode}.json` → `areaName` 一致エリア（なければ weathers を持つ先頭）の当日天気 → `Normalize`（全角空白 U+3000 除去）→ 「{areaName}は、{天気}、という予報です。」。取得不可は `WeatherFetchFailed`。
- `NewsWeatherProvider`（Infra, **具象クラス**）: `AnnouncementAsync` で news/weather を取得（**失敗は握り潰してフォールバック文言**）、`TemplateExpander` で展開。
- `ResearchConfig`（Infra, record）+ ローダ: `news.rss_url` / `news.max_items` / `weather.area_code`（必須）/ `weather.area_name` / `announcement_template`。`config/research.yaml`（Mac から流用）。`llm_script` は W11 用なので**読み飛ばす**（`IgnoreUnmatchedProperties`）。
- App デモ `news`: 実データで原稿生成 → 表示（音声なし）。
- テスト（Infra）: NewsRss（見出し抽出/メディア名除去/件数制限/失敗時例外）/ Jma（抽出+正規化/失敗時例外）/ NewsWeatherProvider（合成/フォールバック）/ ResearchConfig（ロード/必須欠落/既定値）。

**設計判断（interface を作らない）**: Mac の `AnnouncementProviding` protocol は Win design.md §2 の抽象境界リストに**無い**。`NewsWeatherProvider` は具象クラスとする（§3-5）。第 2 実装（LLM 原稿、W11）が現れた時点で seam 化を判断する。

**out（後続）**: LLM ニュース会話化（`LlmNewsScriptProvider` / `NewsScriptStyle` / `target_minutes` 等）→ W11 ／ 放送進行への組込み → W7。

## 3. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（ネットワーク非依存、fake IHttpClient で検証）。
- 実機: `dotnet run --project AIRadio.App -- news` で、実際のニュース見出し + 東京の天気が定型原稿に展開されて表示される（取得失敗時はフォールバック文言）。

## 4. 実装メモ（C# 固有 / Mac との差）
- RSS パース: Mac `XMLParser`（SAX デリゲート）→ **`System.Xml.XmlReader`**（`inItem`/`inTitle` 状態 + Text/CDATA バッファ。Mac のデリゲートと同じ状態遷移）。`DtdProcessing.Ignore`。
- 天気 JSON: `System.Text.Json` + `JsonPropertyName`（`timeSeries`/`areas`/`area`/`name`/`weathers`）。
- `CleanTitle`: `Split(" - ")` で末尾要素を落として再連結（Mac の `components(separatedBy:)` と同じ）。
- fail-tolerant: Mac の `try?` → `try/catch`。ただし `OperationCanceledException` は再 throw（停止は伝播。完全静寂の後始末は呼び出し側）。それ以外の取得失敗のみフォールバック。
- `ResearchConfig` 必須欠落（`weather.area_code`）は `ConfigException.MissingField`（fail-fast、起動時設定不正）。
