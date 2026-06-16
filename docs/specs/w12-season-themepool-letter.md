# W12 — 季節コンテキスト + テーマプール付きフリートーク + お便りコーナー

> 対応 Mac 版: `Sources/AIRadioCore/SeasonPhrases.swift` / `ListenerLetter.swift`（`ListenerLetter` + `ListenerLetterGenerator`）/
> `DialogueScriptGenerator.swift`（dateContext / letter 注入）/ `CornerEngine.swift`（テーマ選択 + letter 3 段）/ `Dialogue.swift`（`CornerTemplate.themePool`）/
> `Sources/AIRadioInfra/CornersConfig.swift`（`themes:`）/ `config/corners.yaml` / `config/program.yaml`。Mac 仕様 `docs/specs/s12-themed-talk-letters.md`。
>
> **W12 は Mac s12 を移植する**。連続放送（W13）に向けたコーナーの多様化。番組構成は固定 1 周のまま、コーナーの中身を拡充する。

## 1. 概要

3 点を 1 スライスで実装する:

1. **季節コンテキストの注入**: 台本生成プロンプトに現在の日付と季節を渡し、「6 月に春めいた話」のような**季節ズレを防ぐ**。
2. **テーマプール付きフリートーク**: コーナーにテーマのプール（お酒/料理/スイーツ/旅行/…）を持たせ、準備のたびに**ランダムに 1 つ選ぶ**。
3. **お便りコーナー**: LLM が架空リスナーのお便り（ラジオネーム + 本文）を生成 → DJ 二人が読み上げて感想を話す → お便りに合うリクエスト曲を 1 曲流す。

## 2. スコープ

**in**:
- Core `SeasonPhrases`（静的クラス）: `Phrase(int month) → string`（月→季節の言い回し、ハードコード）+ `DateContext(DateTimeOffset date, TimeZoneInfo? timeZone = null) → string`（例「今日は6月12日、梅雨の時期です。」、ゼロ埋めなし）。Mac `SeasonPhrases` 移植。
- Core `ListenerLetter`（record `RadioName`/`Body`）+ `ListenerLetterGenerator`（class, `ILLMBackend` + temperature=0.9）: `GenerateAsync(theme, dateContext, ct)` → `MakeRequest`（架空のお便り 1 通・1 行目=ラジオネーム・2 行目以降=本文 200〜400 字・季節合わせ・装飾なし）→ `Parse`（1 行目=ラジオネーム〔ラベル除去〕、残り=本文、空なら `E-LLM-SCRIPT-PARSE-FAILED-001`）。Core 1 ファイル `ListenerLetter.cs` に集約（§3-5）。
- Core `CornerTemplate` に `ThemePool`（`IReadOnlyList<string>?`、末尾任意・既定 null=空扱い）追加。`CornerFormat.Letter` は**既存**（W6 で enum 宣言済み）。
- Core `DialogueScriptGenerator` 拡張: `MakeRequest`/`GenerateAsync` に `dateContext = ""` と `letter (ListenerLetter?) = null` を追加。
  - letter ありなら `# テーマ` を `# リスナーからのお便り`（ラジオネーム + 本文）に差し替え。
  - dateContext 非空なら `# 今日の日付と季節` セクション + 制約「季節や時候の話は、上の日付・季節に合わせる。」を追加。
  - 曲振り指示の letter 変種「コーナーの最後は、{radioName}さんからのリクエスト曲として、…」+ letter 制約 2 本（お便り紹介・読み上げ / 感想）。
  - **greeting / guest / journalContext は対象外**（W13.5/W14/W18）。
- Core `CornerEngine` 拡張:
  - ctor 末尾に `Func<int,int>? randomIndex = null`（既定 `n => Random.Shared.Next(n)`）+ `TimeZoneInfo? timeZone = null`（既定 `Local`）。
  - `SelectTheme(corner)`: `ThemePool` が空/null なら `corner.Theme`、そうでなければ `themePool[randomIndex(count)]`。固定 `corner.Theme` を置換。
  - `dateContext = SeasonPhrases.DateContext(_clock.Now, _timeZone)` を `PrepareAsync` で算出し、letter 生成・台本生成に注入。
  - free_talk 専用の throw を **format 分岐**に置換: `FreeTalk`（従来）/ `Letter`（① お便り生成 → `LetterReady` → ② お便り本文を選曲コンテキストに → ③ 台本に letter 注入）。`Guest`/`ArtistFeature` は引き続き throw（W14/W15）。
- Core `CornerEvent` に `LetterReady(string RadioName)` 追加。
- Infra `CornersConfig`: DTO に `Themes (List<string>?)` 追加 → `CornerTemplate.ThemePool` にマップ（`format: letter` のマップは**既存**）。
- config: `corners.yaml` の `free_talk` に `themes:`（プール）追加 + 新コーナー `letter`（`format: letter`、同プール）。`program.yaml` に `talk(letter)` セグメント追加（OP→song→talk(free_talk)→**talk(letter)**→news→ED）。
- テスト: `SeasonPhrases` 全月 + dateContext / プロンプトの日付・季節・テーマ・letter 注入 / お便りパース（ラベル除去・空 throw）/ letter 準備 3 段 / テーマランダム選択（注入乱数で決定論）/ `CornersConfig`（themes）。

**out（後続・対象外）**:
- 時報リード文 `lead_in`・冒頭挨拶 `greeting`・`CornerContext`・曜日替わり編成 → **W13.5**（Mac s12 corners.yaml の `lead_in` は本スライスでは入れない）。
- ゲストコーナー → W14 / アーティスト特集 → W15（`CornerEngine` で throw 継続）。
- 長期記憶 `journalContext` → W18。
- `DailyContext` 本格版（記念日の軽重）→ W17（W12 は `SeasonPhrases` の月→季節のみ）。
- コーナー数 N 駆動の番組生成・program.yaml v2・「ED で終了」・番組長 UI → W13。

## 3. お便りコーナーの台本構造

```
DJ A: 「ラジオネーム◯◯さんからのお便りです…」（本文を自然に読み上げ）
DJ A/B: お便りへの感想・脱線（テーマ + 季節に沿う）
締め: 「◯◯さんからのリクエスト曲、△△で「□□」」（プレフライト済みの実曲。fallback は曲名伏せ）
```
- 準備順（Mac 同一）: letter = **① お便り生成 → ② リクエスト曲（お便り本文を選曲コンテキストに）→ ③ 台本（読み上げ + 感想 + 曲振り）**。free_talk = ① 選曲 → ② 台本。いずれも曲紹介テキスト生成の**前**にプレフライト確定（§3-2）。

## 4. 季節区分（`SeasonPhrases`、ハードコード）

| 月 | 言い回し | 月 | 言い回し |
|---|---|---|---|
| 1 | 冬、正月明け | 7 | 盛夏 |
| 2 | 冬の終わり | 8 | 真夏、残暑 |
| 3 | 春の始まり | 9 | 初秋 |
| 4 | 春 | 10 | 秋 |
| 5 | 初夏、新緑の季節 | 11 | 晩秋 |
| 6 | 梅雨の時期 | 12（default） | 冬、年の瀬 |

- 月 12 は明示 case なし＝`default`（1〜11 以外＝12 や範囲外も「冬、年の瀬」）。Mac 忠実。
- `DateContext` 書式: `今日は{month}月{day}日、{Phrase(month)}です。`。月日は**ゼロ埋めなし**（`InvariantCulture`）。Gregorian + 注入 timeZone（既定 Local。W8 `TimePhrases` と同方針）。

## 5. 設計判断（確定）

- **乱数 seam = `Func<int,int>` クロージャ**（count → 0..count-1 のインデックス）。Mac の `@Sendable (Int)->Int` を忠実移植。**新 `IRandom`/`IRandomSource` interface は作らない**（§3-5。Mac もプロトコルでなくクロージャ。`onEvent` の `Action<>` と同じ delegate seam）。既定 `n => Random.Shared.Next(n)`、テストは `_ => 0` を注入して決定論化。
- **dateContext の供給元 = `SeasonPhrases.DateContext(_clock.Now, _timeZone)`**（`CornerEngine` 内で算出）。Mac 現行は `DailyCalendar.context(...)`（記念日込み＝s17）だが、Win は s12 段階なので `SeasonPhrases` を導入。記念日版は **W17** で `SeasonPhrases` を内包する `DailyCalendar` に差し替え予定。
- **`CornerEngine` 内で format 分岐**（新 `LetterCornerEngine` は作らない、§3-5）。`PrepareAsync(corner, djs, ct)` のシグネチャは不変（dateContext は内部算出。`CornerContext` は W13.5）。
- **`CornerTemplate.ThemePool` は末尾任意 positional**（`IReadOnlyList<string>? = null`）で既存呼び出し非破壊。consumer は null/空を「`Theme` 固定」と解釈。
- **エンジン・Composition 無改修**: letter は `talk` セグメント + `corner_id: letter` で W7 `BroadcastEngine` 経路にそのまま乗る（`ProgramConfig` の talk 解析は変更不要）。`CornerEngine` の新 ctor 引数は既定で動くため `BroadcastComposition` も無改修（production は `Random.Shared` / `Local`）。
- `ListenerLetter` 本文長 200〜400 字・temperature 0.9 は Mac 同様**コード内**（プロンプト固定値。§3-3 は表示文言の yaml 集約が主眼で、Mac 忠実のためプロンプト定数は inline）。

## 6. 配線（Core / Infra / App）

- **Core 新規**: `SeasonPhrases.cs`（静的）、`ListenerLetter.cs`（`ListenerLetter` record + `ListenerLetterGenerator`）。
- **Core 変更**: `Dialogue.cs`（`CornerTemplate.ThemePool`）、`DialogueScriptGenerator.cs`（dateContext + letter）、`CornerEngine.cs`（randomIndex + timeZone + `SelectTheme` + format 分岐 + letter 3 段）、`CornerEvent.LetterReady`。
- **Infra 変更**: `CornersConfig.cs`（`Themes` DTO + ThemePool マップ）。
- **config**: `corners.yaml`（themes + letter コーナー）、`program.yaml`（talk(letter) セグメント）。
- **無改修**: `BroadcastEngine` / `BroadcastComposition` / `ProgramConfig` / `SongPicker`。`DemoRunner` の `corner` デモは free_talk のまま（letter は `broadcast` デモで通る。任意で letter デモ追加可だがスコープ外）。

## 7. テスト

- **`SeasonPhrases`**: `Phrase(1..12)` 全月 + 範囲外（0/13 → 「冬、年の瀬」）。`DateContext`（固定 TZ + FakeClock 相当の DateTimeOffset、例 6/12→「今日は6月12日、梅雨の時期です。」、ゼロ埋めなし）。
- **`ListenerLetterGenerator`**: `MakeRequest`（プロンプトにテーマ・dateContext・「1 行目はラジオネーム」「200 文字以上、400 文字以内」制約）。`Parse`（1 行目=ラジオネーム / ラベル「ラジオネーム:」除去 / 残り=本文 / 装飾除去 / 全空→`E-LLM-SCRIPT-PARSE-FAILED-001` / 本文空→同）。
- **`DialogueScriptGenerator`**: dateContext 非空で `# 今日の日付と季節` + 季節制約が入る / letter ありで `# リスナーからのお便り`（ラジオネーム + 本文）に差し替わり letter 制約 + リクエスト曲振りが入る / letter なし free_talk は従来どおり。
- **`CornerEngine`**: テーマプールから `randomIndex` 注入で決定論選択（`_ => 0` で先頭）、空プールは `Theme` 固定。letter 準備 3 段（お便り生成→選曲コンテキストにお便り本文→台本に letter）。`LetterReady` 発火。free_talk 回帰（既存テスト緑）。guest/artist は throw 継続。
- **`CornersConfig`**: `themes:` 読み込み（ThemePool）/ 省略時空 / `format: letter` 維持。
- 既存保証（free_talk 進行・完全静寂・プレフライト・per-line speaker）は不変。

## 8. 受け入れ条件

- `dotnet build` / `dotnet test` 全グリーン・警告なし（fake 注入、乱数は注入で決定論、ネットワーク・音声非依存）。
- 実機（`-- broadcast`、ユーザー確認）: フリートークが**プールから選ばれたテーマ**で行われ、**季節の言及が当月に合っている**。お便りコーナーが「ラジオネーム付きお便り読み上げ → 感想 → リクエスト曲」で自然に流れる。お便り → ニュースの順で進む。
- エラーコード追加なし（`E-LLM-SCRIPT-PARSE-FAILED-001` 既存再利用）。
