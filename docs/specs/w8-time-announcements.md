# W8 — 時刻・日付入りアナウンス（発話直前の時刻プレースホルダ展開）

> 対応 Mac 版: `Sources/AIRadioCore/TimePhrases.swift`（`TimeOfDay` / `Greetings` / `TimePhrases.values`）/
> `Sources/AIRadioCore/BroadcastEngine.swift`（`expand(_:extra:)` 発話直前展開）/ `Sources/AIRadioCore/TemplateExpander.swift` /
> `config/themes.yaml`（`greetings:` + OP 時刻プレースホルダ）/ `config/research.yaml`（ニュース冒頭の時報）。Mac 仕様 `docs/specs/s8-time-announcements.md`。
>
> **W8 は Mac s8 の「時刻プレースホルダ展開」だけを移植する**。s8 のもう一方（ニュース読み手＝青山龍星）は **W7 で実装済み**（`program.yaml` の `news.dj_id: ryusei`、実機確認済み）。
> design.md §6 の W8 行ラベル「+ 音量/話速」は、**global `playback_volume`/`speed_scale`（`config/tts.yaml`）として W1 で実装・配線済み**であり（`BroadcastComposition.cs` で `VoicevoxTTS(speedScale)`／`NAudioPlayer(playbackVolume)` に注入、Mac と完全一致・per-DJ ではない）、**W8 では扱わない**（2026-06-15 ユーザー確定）。

## 1. 概要

OP・ニュースのアナウンスに、再生時点の日付・時刻と時間帯挨拶を組み込む。文言は **LLM 生成ではなく `config/*.yaml` の固定テンプレ**で、`{greeting}`/`{month}`/`{day}`/`{ampm}`/`{hour}`/`{hour12}`/`{minute}` のプレースホルダを**発話直前に**実時刻で展開する。

- **OP**: 「**（時間帯挨拶）。◯月◯日、午前/午後◯時になりました。** ケイラボAIラジオの時間なのだ。…{first_song}。」
- **ニュース**: 「**時刻は◯時◯分になりました。** ニュースの時間です。{news} 続いて、天気予報です。{weather} 以上、…」
  （日付なし・午前/午後なしの 12 時間表記＝`{hour12}`）

**時刻の正確さ（最重要）**: ローリング先読み準備（W7, window=2）は数分先行するため、時刻文を準備時点や LLM 台本に焼き込むと再生時とズレる。よって**時刻展開はエンジンの発話直前 `Expand` で `_clock.Now` を読んで行う**（Mac `s8 §3`：発話直前展開）。ニュースは Provider が原稿を組み立て（準備時）、エンジンが時刻を被せる**二段展開**。

## 2. スコープ

**in**:
- Core `TimeOfDay`（enum）: 時間帯区分。`Of(int hour24)` 静的ファクトリ。境界 = 朝 `5..11` / 昼 `12..16` / 夜 `default`（17–23 と 0–4＝深夜またぎ）。
- Core `Greetings`（record）: `Morning`/`Afternoon`/`Evening` 文字列（既定「おはようございます」「こんにちは」「こんばんは」）+ `TextFor(TimeOfDay)`。
- Core `TimePhrases`（静的クラス）: `Values(DateTimeOffset date, Greetings? greetings = null, TimeZoneInfo? timeZone = null) → IReadOnlyDictionary<string,string>`。プレースホルダ値辞書を生成する純粋ロジック（§3）。
- Core `BroadcastThemes` に `Greetings Greetings` を追加（OP/news/ED が共有する挨拶設定）。
- Core `BroadcastEngine`: `TimeZoneInfo` を注入（任意・既定 `TimeZoneInfo.Local`）。`Expand` を**時刻マージ版**に変更し、OP/news/ED の発話直前に `TimePhrases.Values(_clock.Now, themes.Greetings, _timeZone)` を `extra`（`{first_song}`）とマージして展開。
- 文言更新: `config/themes.yaml`（`greetings:` ブロック追加 + `opening.announcement` を時刻プレースホルダ化）/ `config/research.yaml`（ニュース冒頭の時報文を復活）。
- Infra `ThemesConfig`: `greetings:` を読み込み `Greetings` を構築（欠落時は既定値）。`BroadcastThemes.Greetings` を供給。
- App `BroadcastComposition`: `ThemesConfig` の `Greetings` を `BroadcastThemes` に配線（`BroadcastEngine` の `timeZone` は既定 `Local` のまま）。
- テスト: `TimeOfDay` 境界 / `TimePhrases` の各値（noon/midnight 境界含む）/ エンジンの発話直前展開（FakeClock で決定論）/ ニュース二段展開 / `ThemesConfig` の greetings ローダ。

**out（後続・本スライス対象外）**:
- ED への時刻組み込み（仕組みは同じ。文言要望が出たら yaml 編集のみ。Mac s8 §2 同様 out）。
- OP/ED の **per-DJ 口上（`by_dj`）と曜日替わりメイン** → **W13.5**（Win は現状 OP 単一・anchor 固定。W8 は単一 `opening.announcement` のまま時刻化）。
- ニュースの **LLM 会話原稿化**（`announcement_template` はあくまで定型。LLM 化は **W11**。W8 では既存の定型 `NewsWeatherProvider` 経路に時報を足すだけ）。
- 音量/話速（W1 済み・Mac 一致。§冒頭注記）。
- `DailyContext` 本格版（暦・記念日・季節）→ W12/W17。

## 3. プレースホルダ仕様（`TimePhrases.Values`）

| キー | 値 | 例（6/12 15:07） |
|---|---|---|
| `{greeting}` | 時間帯挨拶（`Greetings.TextFor(TimeOfDay.Of(hour24))`） | こんにちは |
| `{month}` / `{day}` | 月・日（数値、**ゼロ埋めなし**） | 6 / 12 |
| `{ampm}` | `hour24 < 12 ? "午前" : "午後"` | 午後 |
| `{hour}` | `hour24 % 12`（NHK 式 12 時間＝0–11、`{ampm}` と組で使う。12:xx → 「午後0時」、0:xx → 「午前0時」） | 3 |
| `{hour12}` | 午前/午後なしの 12 時間表記（0:xx→0、12:xx→12、それ以外 1–11） | 3 |
| `{minute}` | 分（数値、**ゼロ埋めなし**） | 7 |

- `hour24` は対象タイムゾーンの時（0–23）。`var local = TimeZoneInfo.ConvertTime(date, timeZone); var hour24 = local.Hour;`。月日分も `local.Month`/`local.Day`/`local.Minute`。
- `{hour}` と `{hour12}` の唯一の差異は**正午**（`hour24==12` → `{hour}="0"` / `{hour12}="12"`）。深夜（`hour24==0`）は両方 `"0"`。13–23 時は両方 1–11 で一致。
- ゼロ埋めなし＝`int.ToString()`（書式指定子なし。例: 分 5 → `"5"`、`"05"` ではない）。Mac の `String(Int)` と一致。
- 未知プレースホルダは `TemplateExpander` 仕様で原文のまま残る（テンプレ誤記が音声で発覚できる）。`{hour12}` を含まない OP テンプレに時刻辞書を渡しても、該当キーが本文に無ければ無置換（害なし）。
- `greetings`/`timeZone` 既定: C# はコンパイル時定数にできないため引数は nullable とし、本体で `greetings ??= new Greetings(); timeZone ??= TimeZoneInfo.Local;`（Mac の `Greetings()` / `.current` 既定に対応。テストは固定 TZ を明示注入して決定論にする）。

## 4. 文言（設定変更）

```yaml
# config/themes.yaml（追加・変更）
# 時間帯ごとの挨拶（{greeting} の値。境界はコード側: 朝5-11時/昼12-16時/夜17-4時）
greetings:
  morning: "おはようございます"
  afternoon: "こんにちは"
  evening: "こんばんは"

opening:
  # （staging は従来どおり track_uri/intro/volume/ducked/outro/tagline）
  # 先頭の固定「こんにちは。」を時刻プレースホルダに置換（以降の本文は不変）。by_dj 化は W13.5。
  announcement: "{greeting}。{month}月{day}日、{ampm}{hour}時になりました。ケイラボAIラジオの時間なのだ。お送りするのはずんだもんなのだ。本日もリスナーの皆さんと、ゆったりした時間を過ごしていきたいと思うのだ。なお、本番組の音声合成にはVOICEVOXを使用しているのだ。それでは早速、1曲目を聴いてもらうのだ。{first_song}。"

# config/research.yaml（W7 で暫定削除した冒頭の時報を復活。{hour12}/{minute} は発話直前展開）
announcement_template: "時刻は{hour12}時{minute}分になりました。ニュースの時間です。{news} 続いて、天気予報です。{weather} 以上、ニュースと天気予報でした。"
```

- `program.yaml` の `news.dj_id: ryusei`・`djs.yaml` の青山龍星（speaker_id 13）は **W7 で導入済み**。本スライスでの変更なし。
- ED（`ending.announcement`）は時刻を入れない（out）。`Expand` は通すが時刻プレースホルダを含まないため無変化。

## 5. 配線（Core / Infra / App）

- **Core `TimePhrases.cs`**（新規・1 ファイルに `TimeOfDay`/`Greetings`/`TimePhrases` を集約。§3-5：spec ごとに namespace/クラスを量産しない）。外部依存ゼロ。
- **Core `BroadcastThemes`**（`Program.cs`）に `Greetings Greetings` を追加。
- **Core `BroadcastEngine`**:
  - フィールド `private readonly TimeZoneInfo _timeZone;`。ctor 末尾に任意引数 `TimeZoneInfo? timeZone = null` を追加 → `_timeZone = timeZone ?? TimeZoneInfo.Local`（既存呼び出しは非破壊）。
  - `Expand` を `static` から**インスタンスメソッド**へ変更し挨拶を受け取る:
    ```csharp
    private string Expand(Greetings greetings, string template, IReadOnlyDictionary<string,string> extra)
    {
        var values = new Dictionary<string,string>(TimePhrases.Values(_clock.Now, greetings, _timeZone));
        foreach (var (k, v) in extra) values[k] = v;   // extra（{first_song}）が衝突時に優先（Mac の merge と同義）
        return TemplateExpander.Expand(template, values);
    }
    ```
  - 呼び出し 3 箇所（`PerformAsync`）を `Expand(themes.Greetings, …, extraValues)` に変更（OP=`themes.OpeningAnnouncement` / news=`script` / ED=`themes.EndingAnnouncement`）。`_clock.Now` を**各セグメントの発話直前**に読むため時報が正確（`extraValues` の `{first_song}` は時刻非依存なので従来どおりループ前に 1 回構築）。
- **Infra `ThemesConfig`**: DTO に `GreetingsDto? Greetings { Morning/Afternoon/Evening }` を追加。Core `Greetings` へマップ。`greetings:` ブロックごと、または各キーが**欠落（null）**したときのみ既定文字列に倒す（Mac `?? defaults` と一致）。**明示的な空文字はそのまま採用**（Mac 完全一致）。`BroadcastThemes` 構築時に `Greetings` を渡す。
- **App `BroadcastComposition`**: `ThemesConfig` 出力の `Greetings` を `BroadcastThemes` に載せる。`BroadcastEngine` の `timeZone` は省略（既定 `Local`）。`DemoRunner` の該当経路（`broadcast`/`news`）も追随。
- **ニュース二段展開（変更なしで成立）**: `NewsWeatherProvider` は準備時に `{news}`/`{weather}` のみ展開し `{hour12}`/`{minute}` は原文のまま残す（未知キー保持）。エンジンが speech 時に `Expand(themes.Greetings, script, …)` で時刻を被せる。`NewsWeatherProvider` 自体は無改修。

## 6. テスト

- **`TimeOfDay`**: `Of(4/5/11/12/16/17/23/0)` → evening/morning/morning/afternoon/afternoon/evening/evening/evening（深夜またぎ＝0–4 と 17–23 が evening）。
- **`TimePhrases.Values`**（固定 TZ を明示注入して決定論）:
  - 6/12 15:07 → `{greeting}=こんにちは, month=6, day=12, ampm=午後, hour=3, hour12=3, minute=7`。
  - 朝 9:05 → `ampm=午前, hour=9, hour12=9, minute=5`（ゼロ埋めなし）、`greeting=おはようございます`。
  - 正午 12:00 → `ampm=午後, hour=0, hour12=12`（{hour} と {hour12} の差異）。
  - 深夜 0:30 → `ampm=午前, hour=0, hour12=0, greeting=evening`。
  - カスタム `Greetings` を渡すと `{greeting}` がそれを反映。
  - `timeZone` を変えると `hour` 等が変わる（TZ 注入の検証）。
- **`BroadcastEngine`**（fake 注入・FakeClock 固定時刻 + 固定 TZ）:
  - OP テンプレ `"{greeting}。{month}月{day}日、{ampm}{hour}時。{first_song}"` が実時刻 + first_song で展開される。
  - news 原稿に残った `{hour12}`/`{minute}` が speech 時に展開される（二段展開＝Provider が news/weather だけ展開した文字列を渡す fake で確認）。
  - ED テンプレに時刻プレースホルダが無ければ無変化（リグレッション。時刻文字列が一切混入しない）。
  - `extra`（first_song）が時刻キーと**共存**して両方展開される（OP テストで確認）。なお衝突時 extra 優先のマージ規則は Mac と同義だが、`extra` は常に `first_song` のみで時刻キー（greeting/month/day/ampm/hour/hour12/minute）とは決して衝突しないため、衝突分岐は構造上到達不能（テスト不要・コメントで明記）。
- **`ThemesConfig`**: `greetings:` 読み込み（3 値）/ ブロック欠落で既定 / 一部キー欠落（null）で当該のみ既定 / **明示的空文字はそのまま採用**（Mac 一致）/ 既定 TZ（Local）経路の健全性（不変条件のみ）。
- 既存保証（`{first_song}`・critical OP・キャンセル静寂・news 専任読み手 ryusei・終端二重確認）は不変。

## 7. 受け入れ条件

- `dotnet test AIRadio.slnx` 全グリーン（時刻系は FakeClock + 固定 TZ で決定論的に検証）、警告なし。
- 実機（`-- broadcast`、ユーザー確認）:
  - OP が「（時間帯挨拶）。◯月◯日、午前/午後◯時になりました。〜」と**実時刻**で読まれる。
  - ニュースが「時刻は◯時◯分になりました。ニュースの時間です。〜」と**実時刻**で読まれる。
  - ローリング準備のズレを受けず、時刻が**再生時点で正確**。
- エラーコード追加なし（時刻展開は fail しない純粋処理。`greetings` 欠落は既定で吸収）。
