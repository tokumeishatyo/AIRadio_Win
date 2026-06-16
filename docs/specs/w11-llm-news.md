# W11 — ニュースの LLM 会話化（アナウンサー原稿の生成）

> 対応 Mac 版: `Sources/AIRadioCore/NewsScriptGenerator.swift` / `Sources/AIRadioInfra/LlmNewsScriptProvider.swift` /
> `Sources/AIRadioInfra/ResearchConfig.swift`（`NewsScriptStyle`）/ `config/research.yaml`（`llm_script`）。Mac 仕様 `docs/specs/s11-llm-news.md`。
>
> **W11 は Mac s11 を移植する**。ニュース本文を「見出しの羅列＋定型文」から **LLM が生成する自然なアナウンサー原稿**に変える。
> 読み手は引き続き **青山龍星 1 人**（アナウンサースタイル。DJ 二人会話化は Mac s11 §2 で out）。

## 1. 概要

ニュースセグメントの**本文だけ**を LLM 生成にする。**時報イントロと締めのアウトロは固定文**（正確性が必要なため LLM に渡さない）。

- **構造**: `[固定イントロ] {LLM本文} [固定アウトロ]`（下記 §3）。
- **fail-tolerant**: LLM 不調時は現行の定型テンプレ原稿（W5 の `announcement_template`）に**自己完結で**倒して放送継続。素材（news/weather）の個別取得失敗も従来どおりフォールバック文言。**停止（OCE）は伝播**（§3-1）。
- **先行準備**: 生成に数秒かかるため W7 のローリング先読み準備（news セグメントは放送開始時に並行 Task で `AnnouncementAsync` 起動）にそのまま同乗。デッドエアを出さない。**エンジンは無改修**（news seam = デリゲートのまま）。
- **時刻展開**: イントロ・フォールバックに含まれる `{hour12}`/`{minute}` は **W8 の発話直前二段展開**がそのまま処理（本スライスでは展開しない＝原文保持で返す）。

## 2. スコープ

**in**:
- Core `NewsScriptGenerator`（**静的クラス・純粋ロジック**。Mac の enum-namespace 相当。`DialogueScriptGenerator`(W6) の流儀を踏襲するが LLM 呼び出しは持たない）:
  - `MakeRequest(news, weather, persona, targetCharacters, styleHint = "", temperature = 0.6) → LLMRequest`: 素材 + 読み手ペルソナからプロンプト構築。出力契約 = **本文のみ**（見出し番号・記号装飾・ナレーション指示・挨拶・時刻日付・「ニュースの時間です」「以上です」等の定型句を禁止＝前後の固定文と二重になるため）。長さは `targetCharacters` 文字以上・`targetCharacters * 12 / 10` 文字以内（**整数除算**）。
  - `Sanitize(raw) → string`: 行頭マークダウン装飾（`#`/`*`/`-`/`>`/`•`）除去 + `**` 除去 + 空行除去、`Whitespace.TrimHorizontal` 使用、残行を半角スペースで連結。実質空なら `E-LLM-EMPTY-RESPONSE-001`（`LlmException.EmptyResponse`）。
- Infra `NewsScriptStyle`（record）: `StyleHint`/`TargetMinutes`/`CharsPerMinute`/`Intro`/`Outro` + 算出プロパティ `TargetCharacters = TargetMinutes * CharsPerMinute`。既定 intro/outro は固定文（§4）。
- Infra `LlmNewsScriptProvider`: news/weather 取得（fail-tolerant、OCE 伝播）→ `NewsScriptGenerator.MakeRequest`/LLM/`Sanitize` → `{Intro} {body} {Outro}` 組み立て。**LLM 失敗時は `fallbackTemplate`（= `announcement_template`）を `{news}`/`{weather}` 展開して返す（自己完結。NewsWeatherProvider 非依存＝Mac 同一）**。`AnnouncementAsync(CancellationToken) → Task<string>`（NewsWeatherProvider と同形＝エンジンのデリゲート seam にそのまま差し込める）。
- Infra `ResearchConfig`: `llm_script:` ブロックを読み込み `NewsScriptStyle` を構築（各キー欠落時は既定値）。新フィールド `LlmScript`。`announcement_template` は従来どおり（フォールバック原稿として使う）。
- config `research.yaml`: 現在コメントアウト中の `llm_script:` を有効化（`style_hint`/`target_minutes`/`chars_per_minute`。intro/outro は省略＝既定）。
- App `BroadcastComposition`: news provider を `NewsWeatherProvider` → **`LlmNewsScriptProvider`** に差し替え（ペルソナ = news セグメントの `dj_id`、なければ `anchor_dj_id` の persona）。エンジンへの `newsAnnouncement: token => newsProvider.AnnouncementAsync(token)` は不変。
- App `DemoRunner`（`news` デモ）: LLM キーがあれば `LlmNewsScriptProvider`、なければ `NewsWeatherProvider`（フォールバック動作の確認を兼ねる。Mac `runNewsDemo` 同様）。
- テスト: プロンプト構築（素材・ペルソナ・長さ・禁止事項・styleHint 有無）/ Sanitize（装飾除去・空 throw・連結）/ フォールバック（LLM 失敗 → テンプレ原稿）/ 組み立て（intro + body + outro）/ OCE 伝播 / `NewsScriptStyle` 算出 / `ResearchConfig` の `llm_script` ローダ。

**out（後続・対象外）**:
- ニュースの DJ 二人会話化（アナウンサースタイル採用のため不要。要望が出たら別スライス。Mac s11 §2 out）。
- LLM ニュースの先行準備機構自体（W7 のローリング準備で既に成立。**エンジン無改修**）。
- `IAnnouncementProvider` interface 化（§5 参照。デリゲート seam 維持・単一消費者のため作らない）。
- 番組の連続ループ・13 項目拡張は W13 系。

## 3. 原稿の構造

```
[固定イントロ]  時刻は{hour12}時{minute}分になりました。ニュースの時間です。   ← NewsScriptStyle.Intro（既定）
[LLM 本文]      見出しを自然につないだ語り + 短いコメント + 天気への一言（約 2 分）  ← NewsScriptGenerator で生成・整形
[固定アウトロ]  以上、ニュースと天気予報でした。                              ← NewsScriptStyle.Outro（既定）
```

- 連結は `$"{Intro} {body} {Outro}"`（半角スペース区切り、Mac `"\(intro) \(body) \(outro)"` 同一）。
- `{hour12}`/`{minute}` は本スライスでは未展開（原文保持）。エンジンが発話直前に展開（W8 二段展開）。
- LLM 失敗時のフォールバック = `announcement_template` を `{news}`/`{weather}` 展開（イントロ・アウトロは template 内に内蔵済み。`{hour12}`/`{minute}` は同じく原文保持→エンジン展開）。

## 4. 文言（設定・既定値）

```yaml
# config/research.yaml（llm_script を有効化。announcement_template は従来どおり＝フォールバック原稿）
announcement_template: "時刻は{hour12}時{minute}分になりました。ニュースの時間です。{news} 続いて、天気予報です。{weather} 以上、ニュースと天気予報でした。"

llm_script:
  style_hint: "落ち着いた低めのトーンで、聞き手に語りかけるように簡潔に"
  target_minutes: 2
  chars_per_minute: 320
  # intro / outro は省略時の既定を使用（時報イントロ / 「以上、ニュースと天気予報でした。」）
```

- `NewsScriptStyle` 既定: `StyleHint=""` / `TargetMinutes=2` / `CharsPerMinute=320`（= `TargetCharacters` 640）/ `Intro="時刻は{hour12}時{minute}分になりました。ニュースの時間です。"` / `Outro="以上、ニュースと天気予報でした。"`（Mac 完全一致）。
- intro/outro は **YAML で上書き可**だが既定はコード側（Mac 同一。固定文＝正確性のため LLM には渡さない）。
- ペルソナ: `djs.yaml` の青山龍星（`ryusei`）persona。W7 の `program.yaml` `news.dj_id: ryusei` から解決（なければ anchor）。
- `NewsScriptGenerator.MakeRequest` の **temperature 既定 0.6**（グローバル LLM temperature とは独立。Mac 同一。`LlmNewsScriptProvider` は temperature を渡さず既定 0.6 を使う）。

## 5. 配線（Core / Infra / App）

- **Core `NewsScriptGenerator.cs`**（新規・静的クラス 1 ファイル。§3-5）。`MakeRequest`/`Sanitize` のみ。LLM 呼び出しは持たない（Provider 側）。`Whitespace.TrimHorizontal`（W4/W6 共用）使用。
- **Infra `LlmNewsScriptProvider.cs`**（新規）+ `NewsScriptStyle`（`ResearchConfig.cs` に同居 or 近接 1 ファイル）。fail-tolerant: `TryFetchAsync`（OCE 伝播・他は握り潰し）を W5 `NewsWeatherProvider` と同様に持つ。LLM 失敗（非 OCE）は `try/catch` で `fallbackTemplate` 展開に倒す。**OCE は `catch (OperationCanceledException) { throw; }` で必ず伝播**（§3-1。Mac は generic catch で握り潰すが、Win は §3-1 と W5 `NewsWeatherProvider` の既存契約・テストに合わせ伝播。停止時の観測可能挙動は同一＝無音）。
- **Infra `ResearchConfig`**: DTO に `LlmScriptDto?`（`style_hint`/`target_minutes`/`chars_per_minute`/`intro`/`outro`）追加 → `NewsScriptStyle` へマップ（各キー欠落は既定）。record に `NewsScriptStyle LlmScript` 追加。`FromYaml` を更新。
- **App `BroadcastComposition`**: `var newsProvider = new NewsWeatherProvider(...)` を `LlmNewsScriptProvider(news, weather, llm, persona, research.LlmScript, research.AnnouncementTemplate)` に差し替え。persona = `Segments` の最初の news セグメント `DjId` ?? `AnchorDjId` → `djs` の persona。`llm`（`GeminiLLMBackend`）は既存構築物を再利用。エンジン注入のデリゲートは不変。
- **App `DemoRunner`**（`news`）: llm.yaml/llm.local.yaml が読めれば `LlmNewsScriptProvider`（persona=ryusei）、ダメなら `NewsWeatherProvider`（注記ログ）。W5 文言を W11 に更新。
- **`NewsWeatherProvider` は残置**（no-LLM デモのフォールバック経路・概念上のテンプレ原稿源。削除しない＝Mac 同一）。
- **エンジン無改修**: news seam は `Func<CancellationToken,Task<string>>` のまま。**§3-4 seam 決定の確定**: W11 で 2 実装目（`LlmNewsScriptProvider`）が出たが、エンジン seam は単一消費者でデリゲート注入のため **interface 化しない**（§3-5。両 provider は `AnnouncementAsync(CancellationToken)` 同形を規約で共有、デモは delegate で分岐）。`NewsScriptGenerator` は Core だが LLM 呼び出しを持たないため Core→Infra 依存は発生しない。

## 6. テスト

- **Core `NewsScriptGenerator`**:
  - `MakeRequest`: prompt に news/weather が入る、constraints に「本文のみ」「定型句禁止」等の行、長さ下限 `targetCharacters`・上限 `targetCharacters*12/10`（整数除算、例 640→768）、system に persona + 「ケイラボAIラジオ」「ニュースキャスター」、temperature 既定 0.6。
  - styleHint 非空 → 「語りのスタイル: …」行が付く / 空 → 付かない。
  - `Sanitize`: 行頭 `#`/`*`/`-`/`>`/`•` 除去、`**` 除去、空行除去、半角スペース連結。装飾のみ・全空 → `E-LLM-EMPTY-RESPONSE-001` throw。
- **Infra `LlmNewsScriptProvider`**:
  - 成功: `{Intro} {body} {Outro}` に整形済み LLM 本文が入る（fake ILLMBackend）。intro の `{hour12}`/`{minute}` は**未展開のまま**残る（二段展開はエンジン）。
  - LLM 失敗（fake が throw）→ `announcement_template` を `{news}`/`{weather}` 展開した原稿に倒す（`{hour12}`/`{minute}` 未展開）。
  - 素材取得失敗（news/weather fetch throw）→ フォールバック文言で本文生成（fail-tolerant）。
  - **OCE 伝播**（fetch/LLM が OperationCanceledException → 握り潰さず throw、§3-1）。
- **Infra `NewsScriptStyle` / `ResearchConfig`**: `TargetCharacters = TargetMinutes*CharsPerMinute`。`llm_script` 読み込み（style_hint/target_minutes/chars_per_minute）/ ブロック欠落で既定（intro/outro 既定・640 字）/ 一部キー欠落で当該のみ既定。
- 既存保証（news 専任読み手 ryusei・ローリング先読み・完全静寂・W8 二段時刻展開）は不変。

## 7. 受け入れ条件

- `dotnet build` / `dotnet test` 全グリーン・警告なし（fake 注入、ネットワーク・音声非依存）。
- 実機（`-- broadcast`、ユーザー確認）: ニュースが「見出しの羅列」ではなく**自然な語り + コメント**になっている。時報「時刻は◯時◯分になりました。ニュースの時間です。」と締め「以上、ニュースと天気予報でした。」は従来どおり正確（固定文）。ニュース前のデッドエアが発生しない。
- `config/llm.local.yaml` を一時的に外す/壊すと従来テンプレ原稿で放送継続（フォールバック。実機は任意＝テストで検証済み）。
- エラーコード追加なし（`E-LLM-EMPTY-RESPONSE-001` 既存、LLM 失敗は fail-tolerant でフォールバック）。
