# W17 — DailyContext 拡張（曜日名・記念日の軽重）

Mac 版 `s17`（`../AIRadio_Mac-main/docs/specs/s17-daily-context.md` / `DailyCalendar.swift` / `CalendarConfig.swift`）の Windows 移植。
W12 で入れた**季節・日付コンテキスト**（`SeasonPhrases.DateContext`）を拡張し、**曜日名**と**記念日（重要度つき）**を
台本生成プロンプトの dateContext に注入する。記念日は `significance` で扱いを変える:
- **`high`（祝日級）**: 番組全体に波及（各コーナーが意識して織り込む）。
- **`low`（軽い暦）**: 軽く触れる程度にとどめる。

W15 §18-31 で意図的に縮約していた特集の日付コンテキスト（`SeasonPhrases.DateContext` 暫定）を、本スライスで
`DailyCalendar.Context` に差し替えて **Mac parity を回復**する。**W16「DJ の今日の気分」も同じ dateContext を材料にするため自動的に厚みが増す**（追加実装不要）。

> 進め方は確立パターン（CLAUDE.md §7）: 本 spec 凍結 → 仕様検証 Workflow → 実装 → 多角レビュー →
> `dotnet test AIRadio.slnx` 緑 → commit/push → Confluence ミラー。
> ※ file:line は 2026-06-18 時点（W16 commit `e311fc0` 後）の指標。実装時に現物で再確認する。
> ※ **挙動の正は Mac shipped コード**（Core `DailyCalendar.swift` ＋ Infra `CalendarConfig.swift`）。ローダ規則は §4 で Mac 逐語に揃える。

---

## 0. スコープ

### 入れる（Mac s17 §4 ＋ shipped `DailyCalendar.swift` / `CalendarConfig.swift`）
1. **新 Core 型**（新規 `AIRadio.Core/DailyCalendar.cs`）: `AnniversarySignificance`（High/Low）/ `Anniversary`（Month/Day/Name/Significance）/ `DailyCalendar`（WeekdayNames/Anniversaries ＋ `Context(date, tz)` ＋ `Standard`/`StandardWeekdayNames`）。
2. **新 Infra ローダ** `DailyCalendarConfig`（`config/calendar.yaml` → `DailyCalendar`）。**Mac `CalendarConfig.swift` 逐語移植**: `weekday_names`（キー省略→Standard ／ present は空配列含め 7 要素必須）と `anniversaries`（`date:"MM-DD"`/`name`/`significance`）。fail-fast は `weekday_names` present≠7 ・`date` 形式不正/範囲外 ・`name` 欠落/空 ・`significance` が `high`/`low`/省略 **以外の非空文字列** ・壊れ yaml（CFG・既存 `E-CFG-MISSING-FIELD-001` 再利用、新コードなし）。**`significance` 省略は Low 既定**（Mac 一致・throw しない）。
3. **config**（新規 `config/calendar.yaml`・コミット）: Mac `config/calendar.yaml` を**そのまま流用**（標準曜日名 ＋ 主要祝日 high ＋ 季節行事 ＋ 軽い記念日 low、計 16 件）。
4. **配線**: `CornerEngine`・`ArtistFeatureEngine` に `DailyCalendar` を注入し、両者の `SeasonPhrases.DateContext(_clock.Now, _timeZone)` 呼び出しを `_calendar.Context(_clock.Now, _timeZone)` に置換。`BroadcastComposition` が `calendar.yaml` をロードして両エンジンへ注入。

### 入れない（後続／スコープ外）
- **news への記念日注入**（`NewsScriptGenerator` は dateContext を受けない＝「時刻・日付の定型句を書かない」設計。W11）。対象外。
- 季節表（month→phrase）の config 化（既存ハードコード `SeasonPhrases.Phrase`。将来 `prompts.yaml`/可搬性作業で）。
- 記念日の網羅整備（Mac 流用の最小セットから始め、運用で `calendar.yaml` に追記）。
- 長期記憶 journalContext = **W18**。

> **既存下地（編集不要）**: 消費側 `DialogueScriptGenerator`（`# 今日の日付と季節` セクション・`DialogueScriptGenerator.cs:61-64,184-186`）/ `ListenerLetter`（`ListenerLetter.cs:36`）/ `MakeArtistFeatureRequest` は **dateContext 文字列を受け取るだけ**＝無改変で拡張に乗る。`SeasonPhrases.Phrase`（`SeasonPhrases.cs:13-27`）は `DailyCalendar.Context` が流用。

---

## 1. 現状（実装前検証済み）

- `SeasonPhrases.DateContext(date, tz)`（`SeasonPhrases.cs:34-39`）が「今日は{m}月{d}日、{Phrase(m)}です。」を生成（月日ゼロ埋めなし・`TimeZoneInfo.ConvertTime`・既定 tz=Local）。
- 生成箇所は **2 つ**: `CornerEngine.PrepareAsync`（`CornerEngine.cs:123`）と `ArtistFeatureEngine.PrepareAsync`（`ArtistFeature.cs:167`）。本スライスでこの 2 行を `_calendar.Context(...)` に置換。
- 消費は文字列受け取りのみ（無改変）。news は dateContext 非受領（対象外）。
- `SeasonPhrases.DateContext` 自体は**残置**（`DailyCalendar.Context` が `SeasonPhrases.Phrase` を流用。`SeasonPhrasesTests` の `DateContext` 直叩きテストは不変）。両エンジンが呼ばなくなるだけ。

---

## 2. 新 Core 型（`AIRadio.Core/DailyCalendar.cs`）

```csharp
public enum AnniversarySignificance { High, Low }

public sealed record Anniversary(int Month, int Day, string Name, AnniversarySignificance Significance);

public sealed class DailyCalendar
{
    public IReadOnlyList<string> WeekdayNames { get; }
    public IReadOnlyList<Anniversary> Anniversaries { get; }
    public DailyCalendar(IReadOnlyList<string>? weekdayNames = null, IReadOnlyList<Anniversary>? anniversaries = null);

    public static IReadOnlyList<string> StandardWeekdayNames { get; } // ["日曜日","月曜日","火曜日","水曜日","木曜日","金曜日","土曜日"]（7 要素）
    public static DailyCalendar Standard { get; }                     // 標準曜日名・記念日なし

    public string Context(DateTimeOffset date, TimeZoneInfo? timeZone = null);
}
```

- Mac は `struct`。Win は `Anniversary`=record（値等価でテスト容易）、`DailyCalendar`=`sealed class`（`WeeklyCast` 前例に倣う）。`AnniversarySignificance`=enum（YAML 文字列はローダで map）。
- **曜日 index（本スライス唯一の Win 固有差分）**: Mac は `Calendar.weekday`（1=日..7=土）を `-1` して 0-based 配列を引く。**Win は `TimeZoneInfo.ConvertTime(date, tz).DayOfWeek`（C# `DayOfWeek`＝Sunday=0..Saturday=6）を `(int)` で直接 index**（`WeeklyCast` と同じ TZ 解釈・`-1` 不要）。`StandardWeekdayNames[(int)DayOfWeek.Sunday]="日曜日"`・`[(int)DayOfWeek.Saturday]="土曜日"` で全曜日 Mac と同結果。**他のローダ仕様は Mac `CalendarConfig.swift` 逐語**（§4）。
- **`Context` は Mac `DailyCalendar.swift:47-69` と同一文字列**:
  - ベース: `$"今日は{m}月{d}日"` ＋（weekday 非空なら `$"（{weekday}）"`）＋ `$"、{SeasonPhrases.Phrase(m)}です。"`。月日は `InvariantCulture`・ゼロ埋めなし。weekday は `(int)dow` が `WeekdayNames` の範囲内なら該当名、外なら `""`（Mac `indices.contains` ガード相当）。
  - 記念日一致時（§3）: `High` → `$"今日は『{name}』。番組を通して、{name}にちなんだ話題を意識して織り込んでください。"` ／ `Low` → `$"今日は『{name}』。話の流れで軽く触れる程度にとどめてください。"`（Mac 逐語）。
- **記念日マッチ（private `AnniversaryFor(int month, int day)`）**: `Anniversaries.Where(a => a.Month==month && a.Day==day)` のうち **`High` を優先**（`FirstOrDefault(a => a.Significance==High) ?? matches.FirstOrDefault()`）。代表 1 件のみ言及（Mac s17 §5 / `DailyCalendar.swift:72-75`）。

### 出力例（Mac s17 §3 の prose 例をそのまま転記。**曜日は説明用で実年の曜日と一致しなくてよい**）

| 状況 | 注入される文字列 |
|---|---|
| 記念日なし | 「今日は6月14日（土曜日）、梅雨の時期です。」 |
| `high` | 「今日は5月5日（月曜日）、初夏、新緑の季節です。今日は『こどもの日』。番組を通して、こどもの日にちなんだ話題を意識して織り込んでください。」 |
| `low` | 「今日は7月7日（月曜日）、盛夏です。今日は『七夕』。話の流れで軽く触れる程度にとどめてください。」 |

> ※ **テストでは実在の日付↔曜日ペアを使う**（例: 1970-01-01=木曜・UTC、2026-06-14=日曜）。上表は Mac prose の説明例の転記であり実年曜日とは無関係。

---

## 3. 重要度（significance）の扱い

- 同日に複数一致 → **`high` 優先**（high が 1 件でもあれば high 文、なければ low 文）。代表 1 件のみ（複数列挙は冗長）。一致は month/day で判定。
- 一致なし → 曜日＋季節のみ。
- `high` の波及はテーマ**置換**ではなく「意識して織り込む」誘導（コーナーのテーマと共存）。指示文が毎コーナーのプロンプトに入る＝番組全体に波及。

---

## 4. 新 Infra ローダ（`AIRadio.Infrastructure/Config/DailyCalendarConfig.cs`）

`GuestsConfig`（static class・`YamlConfigLoader.Deserialize<Dto>`・`ConfigException.MissingField`）の形を踏襲し `DailyCalendar` を返す。**Mac `CalendarConfig.swift:18-51` を逐語移植**:

- `FromYaml(string)` / `LoadFile(path)`。DTO: `WeekdayNames: List<string>?`（`weekday_names`） / `Anniversaries: List<AnniversaryDto>?`（`AnniversaryDto { Date: string?; Name: string?; Significance: string? }`）。
- **weekday_names**: **キー省略（DTO で null）→ `StandardWeekdayNames`**。**present（空配列 `[]` を含む）→ 要素数 7 を必須**、不一致（0 含む）は fail-fast。これは **Mac shipped 一致**（`CalendarConfig.swift:22-29` の `if let names { guard names.count == 7 }`＝空配列は非 null ゆえ throw、nil のみ Standard）。**DTO は null（キーなし）と空リストを区別する**（C# の `List<string>?` で `null` 判定）。
- **anniversaries**: 省略/空 → 空リスト。各エントリ（Mac `CalendarConfig.swift:31-48` 逐語）:
  - `date` "MM-DD"（`-` 区切りちょうど **2 部**・各整数。**month 1..12 / day 1..31 の単純範囲**＝Mac と同一。月別日数の厳密判定はしない＝`02-31`/`04-31` も通す。部数≠2・非整数・範囲外は fail-fast）→ Month/Day。
  - `name` 必須・**非空**（欠落/空は fail-fast。Mac は date/name を同一 guard で検証）。
  - `significance`（**case-sensitive・Mac 一致**）: `"high"`→`High` ／ `"low"` **または省略/null**→`Low`（Mac `case "low", .none: .low`＝省略は軽い暦扱い）。**`high`/`low`/省略 以外の非空文字列のみ fail-fast**（例 `"medium"`。大文字 `"High"`/`"LOW"` も throw＝case-sensitive）。**ローダは null 時に明示的に `Low` を代入する**（C# enum 既定は宣言順先頭＝`High` ゆえ、DTO の `string?` を見て null→Low と書く）。
- 壊れ yaml は `YamlConfigLoader.Deserialize` の例外を `ConfigException` に正規化（他ローダ準拠）。
- **エラーコードは既存 `E-CFG-MISSING-FIELD-001`（`ConfigException.MissingField`）を再利用**（W12/W13 が invalid 値にも同コードを用いる Win 既定。新コード追加なし）。

### config（新規 `config/calendar.yaml`・コミット）

Mac `config/calendar.yaml` を**そのまま流用**（コメント含む）。内容（16 記念日）:

```yaml
# 暦設定（仕様 W17）。台本生成プロンプトに「曜日名・記念日」を注入する。
# weekday_names: 曜日名（index = C# DayOfWeek。0=日曜 … 6=土曜の順）。省略時は標準日本語。
# anniversaries: 記念日（date: "MM-DD" → name + significance）。
#   significance: high = 祝日級（番組全体に波及）／ low = 軽い暦（軽く触れる程度）／省略時は low 扱い。
weekday_names: ["日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日"]
anniversaries:
  # 国民の祝日（high）
  - { date: "01-01", name: "元日",         significance: high }
  - { date: "02-11", name: "建国記念の日", significance: high }
  - { date: "04-29", name: "昭和の日",     significance: high }
  - { date: "05-03", name: "憲法記念日",   significance: high }
  - { date: "05-04", name: "みどりの日",   significance: high }
  - { date: "05-05", name: "こどもの日",   significance: high }
  - { date: "11-03", name: "文化の日",     significance: high }
  - { date: "11-23", name: "勤労感謝の日", significance: high }
  # 季節の行事（high）
  - { date: "12-25", name: "クリスマス",   significance: high }
  - { date: "12-31", name: "大晦日",       significance: high }
  # 軽い記念日（low）
  - { date: "02-14", name: "バレンタインデー", significance: low }
  - { date: "03-14", name: "ホワイトデー",     significance: low }
  - { date: "07-07", name: "七夕",             significance: low }
  - { date: "10-31", name: "ハロウィン",       significance: low }
  - { date: "11-22", name: "いい夫婦の日",     significance: low }
```

> Mac s17 §4-3 の命名メモ（`anniversaries.yaml` → `calendar.yaml`）は Mac で `calendar.yaml` 採用済み。Win も `calendar.yaml`。weekday_names のコメントは C# DayOfWeek（0=日曜）に合わせて読み替え。

---

## 5. 配線（`CornerEngine` / `ArtistFeatureEngine` / `BroadcastComposition`）

- **`CornerEngine` ctor**: 末尾（`timeZone` の後）に `DailyCalendar? calendar = null` を追加。`_calendar = calendar ?? DailyCalendar.Standard`。`CornerEngine.cs:123` を `var dateContext = _calendar.Context(_clock.Now, _timeZone);` に置換。
- **`ArtistFeatureEngine` ctor**: 末尾（`onEvent` の後）に `DailyCalendar? calendar = null` を追加。`_calendar = calendar ?? DailyCalendar.Standard`。`ArtistFeature.cs:167` を同様置換。
- **ctor 引数順（Win 固有・無害）**: Mac は `dailyCalendar` を `onEvent` の前に置くが、Win は**末尾 optional 追加**（既存 positional テスト呼び出しを壊さない＝W12/W14/W15 の `randomIndex`/`guests`/`runner` と同方針）。named/optional ゆえ挙動に影響なし。
- **既定が `Standard`（曜日名あり）＝意図的挙動変更**: 既定でも dateContext に曜日が入る。ただし現行の両エンジンテストは dateContext 文字列をアサートしていないため**既存テストの破壊はない**（曜日入り検証は §6 で parity テストを新規追加）。
- **`BroadcastComposition`**: `calendar.yaml` をロードし両エンジンへ `calendar:` で注入。**ファイル不在時は `DailyCalendar.Standard`**（`File.Exists(calendarPath) ? DailyCalendarConfig.LoadFile(calendarPath) : DailyCalendar.Standard`＝guests.yaml と同じ防御的ロード。あるのに壊れていれば fail-fast）。`DemoRunner` 等は既定 `Standard` で無改修。
- 消費側（`DialogueScriptGenerator`/`ListenerLetter`）は無改変。

---

## 6. テスト（`dotnet test AIRadio.slnx` 緑が完了条件）

- **`DailyCalendarTests`（Core 新規）**（固定日付・`TimeZoneInfo.Utc` で決定論）:
  - (a) 記念日なし → 「今日は{m}月{d}日（{曜日}）、{季節}です。」のみ。
  - (b) `High` → 「番組を通して…意識して織り込んでください。」文が付く。
  - (c) `Low` → 「話の流れで軽く触れる程度にとどめてください。」文が付く。
  - (d) 同日複数（low + high）→ `High` 優先・代表 1 件。
  - (e) 曜日名が `WeekdayNames` から正しく引かれる（**実在の日付↔曜日**で検証。例 2026-06-14＝日曜→「日曜日」、別日で土曜→「土曜日」＝(int)Saturday=6 の端境界）。
  - (f) `Standard` は曜日名あり・記念日なし。
- **`DailyCalendarConfigTests`（Infra 新規）**: 正常（weekday_names + anniversaries）／`weekday_names` **キー省略→Standard** ／`weekday_names` **空配列 `[]`・要素数≠7→throw** ／`anniversaries` 省略/空→空リスト ／**`significance` 省略→Low（throw しない＝Mac `significanceDefaultsToLow` 相当）** ／`significance` 非空未知（`"medium"`）→throw ／`significance` 大文字（`"High"`）→throw（case-sensitive）／`date` 形式不正（部数≠2・非整数）→throw ／`date` 範囲外（`13-01`/`05-32`）→throw ／`name` 欠落/空→throw ／壊れ yaml→throw。
- **`CornerEngineTests`（parity テスト新規追加）**: 現行両エンジンテストには dateContext 文字列のアサートが**無い**ため「更新」でなく**新規追加**（Mac `injectsAnniversaryFromDailyCalendar`/`injectsDateContextIntoScriptPrompt` 相当）。`MakeEngine`（`timeZone: TimeZoneInfo.Utc` 固定）＋ `FakeClock` 既定（`UnixEpoch`=1970-01-01・**木曜・UTC**）に `01-01`=`元日`(High) を持つ `DailyCalendar` を注入 → 台本生成 LLM リクエストの prompt に「今日は1月1日（木曜日）、冬、正月明けです。今日は『元日』。番組を通して、元日にちなんだ話題を意識して織り込んでください。」が入ることを検証（Mac は `requests[1].prompt`。Win は SongPicker→台本の呼び出し順を実装時に確認し、台本リクエストの prompt を対象にする）。
- **`SeasonPhrases.DateContext` 直叩きの単体テスト（`SeasonPhrasesTests`/`DialogueScriptGeneratorTests`/`ListenerLetter` 系・曜日なし）は不変**（dateContext を引数で直接渡す経路＝DailyCalendar 非経由）。
- **回帰（不変）**: 完全静寂・ローリング準備・ED で終了・時報の時刻正確性・W13.5/W14/W15/W16・`BroadcastSession`。

---

## 7. 影響ファイル一覧

| 層 | ファイル | 変更 |
|---|---|---|
| Core | `DailyCalendar.cs`（新規）| `AnniversarySignificance`/`Anniversary`/`DailyCalendar`（Context・Standard・AnniversaryFor）|
| Core | `CornerEngine.cs` | ctor 末尾 `DailyCalendar? calendar=null` ＋ `_calendar`、`:123` を `_calendar.Context` に置換 |
| Core | `ArtistFeature.cs` | ctor 末尾 `DailyCalendar? calendar=null` ＋ `_calendar`、`:167` を `_calendar.Context` に置換 |
| Infra | `Config/DailyCalendarConfig.cs`（新規）| `calendar.yaml` → `DailyCalendar`（Mac `CalendarConfig.swift` 逐語の fail-fast 検証）|
| App | `Composition/BroadcastComposition.cs` | `calendar.yaml` ロード（不在は Standard）・両エンジンへ注入 |
| config | `calendar.yaml`（新規・コミット）| Mac `config/calendar.yaml` 流用（曜日名 ＋ 記念日 16 件）|
| docs | `design.md` | W17 行を実装済みに（必要なら注記）|
| Core.Tests | `DailyCalendarTests.cs`（新規）| §6 |
| Core.Tests | `CornerEngineTests.cs` | calendar 注入で high 文がプロンプトに（parity テスト**新規追加**。既存 dateContext アサートは無いため更新不要）|
| Infra.Tests | `DailyCalendarConfigTests.cs`（新規）| §6 |

- 新規エラーコードなし。新規 interface なし（`DailyCalendar` は具象データ型＝実差し替え境界でない、§3-5）。

---

## 8. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。新規エラーコード/interface なし。
- **実機確認（要 VOICEVOX + Spotify Premium・最後に一括）**:
  1. 通常日: コーナーの会話に**曜日・季節が自然に**出る（季節ズレなし）。
  2. 祝日（`calendar.yaml` に当日を `high` 登録）: **複数コーナーでその日にちなんだ話題**が織り込まれる。
  3. 軽い記念日（`low` 登録）: 触れても軽く、深入りしない。
  4. 既存進行（曲・ニュース・お便り・ゲスト・特集・完全静寂・W16 の今日の気分/リード文）に影響なし。

---

## 9. 確定設計判断（W17）／ Win 固有 parity 注記

1. **曜日 index は C# `DayOfWeek`（Sunday=0..Saturday=6）を直接使用**（Mac の `Calendar.weekday-1` と等価・全曜日一致。`-1` 不要）。TZ 解釈は `TimeZoneInfo.ConvertTime(date, tz).DayOfWeek`（`WeeklyCast` 前例）。**本スライス唯一の Win 固有差分**。
2. **`Context` 文字列は Mac `DailyCalendar.swift` と逐語一致**（曜日括弧・記念日 high/low 文・範囲外 weekday は空文字ガード）。
3. **ローダ規則は Mac `CalendarConfig.swift` 逐語**: significance 省略→Low・case-sensitive・date は 1..12/1..31 単純範囲・weekday_names は null のみ Standard で present(空配列含む)は 7 要素必須。**いずれも Mac parity であり Win 防御的追加ではない**。唯一 C# 実装上の注意は「null→Low を明示代入」（enum 既定が High のため）。
4. **`DailyCalendar` は具象データ型**（新 interface を作らない＝§3-5）。エンジンへは ctor 末尾 optional で注入（既存 positional 呼び出し非破壊。Mac の引数順とは異なるが named/optional ゆえ無害）。
5. **既定 `Standard`（曜日名あり・記念日なし）**＝既定でも曜日が dateContext に入る挙動変更。現行エンジンテストは dateContext を非アサートゆえ破壊なし。曜日入り検証は parity テストを新規追加。
6. **`SeasonPhrases.DateContext` は残置**（`DailyCalendar.Context` が `SeasonPhrases.Phrase` を流用。両エンジンが呼ばなくなるだけ。直叩き単体テストは不変）。
7. **`calendar.yaml` は composition で防御的ロード**（不在→Standard・guests.yaml 流儀。あるのに壊れは fail-fast）。
8. **エラーコードは既存 `E-CFG-MISSING-FIELD-001` 再利用**（新カテゴリ/コードなし）。
9. **news は対象外**（dateContext 非受領）。W15 §18-31 の特集 dateContext 縮約は本スライスで `DailyCalendar.Context` 化して Mac parity 回復。

Mac リファレンス: `DailyCalendar.swift`（Context の正）/ `CalendarConfig.swift`（ローダの正）/ `SeasonPhrases.swift`（phrase 流用）/ `CornerEngine.swift:106`・`ArtistFeature.swift:204`（注入点）/ `BroadcastWiring.swift`（calendar.yaml ロード）/ `CornerEngineTests.swift`（`injectsDateContextIntoScriptPrompt`/`injectsAnniversaryFromDailyCalendar`）。SoT: `docs/specs/s17-daily-context.md`。
