# W18 — ステーション・ジャーナル（週次リセットの長期記憶）

Mac 版 `s18`（`../AIRadio_Mac-main/docs/specs/s18-station-journal.md` / `Sources/AIRadioCore/StationJournal.swift` /
`Sources/AIRadioInfra/JournalStore.swift` / `BroadcastEngine.swift` / `DialogueScriptGenerator.swift`）の Windows 移植。

番組に**地続き感**を持たせる長期記憶。**番組を ED で正常終了したときだけ「その回のハイライト」を LLM で短く要約して
ローカル（`config/journal.local.yaml`）に永続化**し、**次回放送の冒頭コーナーのプロンプトに注入**して「前回はこんな話をした」と
軽く振り返れるようにする。肥大を防ぐため **ISO 週（月曜始まり）でリセット**（日曜まで貯め、月曜の最初の放送で前週分を破棄）＋
**ファイルを人為削除すれば即クリア**の二段構え。

> 進め方は確立パターン（CLAUDE.md §7 / メモリ `airadio-slice-runthrough`）: 本 spec 凍結 → 仕様検証 Workflow → 実装 →
> 多角レビュー → `dotnet test AIRadio.slnx` 緑 → commit/push → Confluence ミラー。
> ※ file:line は 2026-06-18 時点（W17 commit `24722c0` 後）の指標。実装時に現物で再確認する。
> ※ **挙動の正は Mac shipped コード**（Core `StationJournal.swift` / `BroadcastEngine.swift`、Infra `JournalStore.swift`）。
> prose と相違したら code 準拠。

---

## 0. スコープ

### 入れる（Mac s18 §12 確定 A–E ＋ shipped コード）
1. **新 Core 型**（新規 `AIRadio.Core/StationJournal.cs`）: `JournalEntry`（Date "YYYY-MM-DD" / Highlight）/
   `StationJournal`（WeekKey / Entries・`WeekKeyFor`・`EntriesForCurrentWeek`・`Appended`・`MaxEntries=7`・`Empty`）/
   `BroadcastDigest`（Date / GuestName? / ArtistName? ＋ `HasContent`）/ `JournalSummarizer`（LLM 要約・決定論フォールバック）。
2. **新 Core 抽象** `IJournalStore`（`Abstractions.cs`）: `StationJournal Load(); void Save(StationJournal)`。
   **design.md §2 が列挙する正規シーム**（テストで fake 差し替え＝`InMemoryJournalStore`。新 interface を作ってよい数少ないケース）。
3. **新 Infra 実装** `YamlJournalStore`（新規 `AIRadio.Infrastructure/Persistence/YamlJournalStore.cs`）: `config/journal.local.yaml`
   への永続化。ファイル無し＝空ジャーナル、壊れていたら **load で throw**（呼び出し側 `BroadcastEngine` が握り潰して空に倒す＝
   事故ゼロ系）。原子的上書きは**確立済み idiom**（`ArtistListGenerator.Write`）に揃える＝一意 temp → 既存は `File.Replace`／初回は `File.Move`・失敗時は temp を best-effort 削除・BOM なし UTF-8。
4. **DialogueScriptGenerator 注入経路**: `MakeRequest`/`GenerateAsync` に `journalContext` 引数（dateContext と同型）を追加。
   非空なら `# 前回までの番組の振り返り` セクションを足し、**冒頭コーナー（greeting 非 null）にのみ**「軽く一言触れる」制約を 1 本追加。
5. **CornerContext 拡張**: `JournalContext` フィールドを追加。`CornerEngine.PrepareAsync` が `context.JournalContext` を generator へ渡す。
6. **BroadcastEngine 配線**: ① 開始時に `journalStore?.Load()` → 当週ハイライト → `journalContext` を**冒頭コーナーの準備にのみ**渡す。
   ② **正常終了時のみ**（`BroadcastFinished` 直前）digest を要約 → `Appended` → `Save`。停止/キャンセルでは保存しない。
7. **App 配線** `BroadcastComposition`: `YamlJournalStore(config/journal.local.yaml)` ＋ `JournalSummarizer(llm)` を作って engine へ注入。
8. **config**: `journal.local.yaml` は**出荷時なし**（gitignore 済み・既に `.gitignore` 行あり）。サンプルも置かない（ユーザーデータ・人為削除でクリア）。

### 入れない（後続／スコープ外）
- **トーク「テーマ」のジャーナル化**（確定 A: 含めない。テーマは `CornerEngine` 側で選ばれ収集に追加配線が要るため、地続き感は
  ゲスト・特集で十分とし簡素を優先）。
- **冒頭以外のコーナーへの注入**（確定 B: 冒頭コーナーのみ。毎コーナーは冗長）。
- 会話の逐語ログ保存・複数番組/複数局のジャーナル・クラウド同期。
- 専用の throw するエラーコード（JNL は**完全 fail-tolerant**＝要約 LLM 失敗・ファイル破損・保存失敗を握り潰す。`error-codes.md` に記載済み）。

> **既存下地（編集不要）**: `.gitignore` に `journal.local.yaml` 行あり。`error-codes.md` に JNL（W18・fail-tolerant・throw コードなし）記載あり。
> `design.md §2` に `IJournalStore` シーム、design テーブルに `YamlJournalStore`（ISO 週キー）記載あり。**消費側 `DialogueScriptGenerator` は
> 既に dateContext と同型のセクション差し込み機構を持つ**＝journalContext も同じ流儀で乗る。

---

## 1. データモデル（Core 新規 `StationJournal.cs`）

```csharp
public sealed record JournalEntry(string Date, string Highlight);   // 1 放送 = 1 エントリ

public sealed record StationJournal
{
    public string WeekKey { get; }
    public IReadOnlyList<JournalEntry> Entries { get; }
    public const int MaxEntries = 7;                                // 週内最大（1 日 1 放送想定）

    public StationJournal(string weekKey = "", IReadOnlyList<JournalEntry>? entries = null);
    public static readonly StationJournal Empty;                    // weekKey="" / entries=[]

    // ISO 週（月曜始まり）のキー "YYYY-Www"（例 "2026-W24"）。
    public static string WeekKeyFor(DateTimeOffset now, TimeZoneInfo? timeZone = null);
    // 当週一致で Entries、違えば空（週替わりで忘れる。§5）。
    public IReadOnlyList<JournalEntry> EntriesForCurrentWeek(DateTimeOffset now, TimeZoneInfo? timeZone = null);
    // 追記。週替わりなら過去を空にしてから追記し、MaxEntries 超は古い順に落とす（リングバッファ）。
    public StationJournal Appended(JournalEntry entry, DateTimeOffset now, TimeZoneInfo? timeZone = null);
}
```

- **ISO 週キー（Win 固有・本スライス唯一の実装差分）**: Mac は `Calendar(identifier: .iso8601)` の `yearForWeekOfYear`/`weekOfYear`。
  **Win は `System.Globalization.ISOWeek`**: `local = TimeZoneInfo.ConvertTime(now, tz).DateTime` → `$"{ISOWeek.GetYear(local):D4}-W{ISOWeek.GetWeekOfYear(local):D2}"`。
  `ISOWeek` は ISO 8601（月曜始まり・週番号）を実装するため Mac と同結果。既定 tz は `TimeZoneInfo.Local`（Mac の既定 `.current` 相当）。
- `EntriesForCurrentWeek`: `WeekKey == WeekKeyFor(now, tz)` なら `Entries`、違えば `Array.Empty`（Mac 逐語）。
- `Appended`: `currentWeek = WeekKeyFor(now, tz)`。`next = (WeekKey == currentWeek) ? Entries : []` をコピー → `entry` 追記 →
  `next.Count > MaxEntries` なら先頭から `Count - MaxEntries` 件 `RemoveRange` → `new StationJournal(currentWeek, next)`（Mac 逐語）。
- **値等価の注意**: `JournalEntry` は positional record（要素値等価）。`StationJournal` は record だが `Entries`（`IReadOnlyList`）は
  参照等価のため、**テストは `WeekKey` と `Entries`（要素列）を個別に検証**する（whole-object `==` に依存しない）。`StationJournal` の equality は
  オーバーライドしない（§3-5 の不要な複雑化回避）。

---

## 2. ハイライトの収集と要約（Core）

```csharp
public sealed record BroadcastDigest(string Date, string? GuestName = null, string? ArtistName = null)
{
    public bool HasContent => GuestName is not null || ArtistName is not null;   // ゲストも特集も無い回は記録しない
}

public sealed class JournalSummarizer        // fail-tolerant＝throw しない
{
    public JournalSummarizer(ILLMBackend llm, double temperature = 0.6);
    public Task<string> SummarizeAsync(BroadcastDigest digest, CancellationToken ct = default);
    internal static string Facts(BroadcastDigest digest);     // "ゲスト: {g}\nアーティスト特集: {a}"
    internal static string Fallback(BroadcastDigest digest);  // 決定論テンプレ（素材無し→""）
}
```

- **収集対象は日付・ゲスト名・特集アーティスト名のみ**（確定 A。テーマは含めない）。BroadcastEngine が `selectedGuest`/`selectedArtist`/`_clock.Now` から digest を作る。
- `SummarizeAsync`: Mac `summarize` 逐語。プロンプト（**temperature 0.6・global 温度と独立**、Mac 一致）:
  - prompt = `"次回のラジオ放送の冒頭で「前回はこんな放送でした」と軽く振り返るための、短い記録文を 1 文で書いてください。\n" + Facts(digest) + "\n- 出力は記録文 1 文のみ。日付・「以上」などの定型句や記号装飾は書かない。長くしない。"`
  - system = `"あなたはラジオ番組の記録係です。次回の振り返りに使う短い一文を書きます。"`
  - LLM 応答を `.Trim()`（Mac `.whitespacesAndNewlines`＝改行も除去）→ 非空ならそれ、空/失敗なら `Fallback`。**LLM 失敗（例外・空）は握り潰して Fallback**（Mac `try?` 相当＝OCE 含め全例外を catch）。
- `Facts`: ゲスト/特集の各行（無いものは出さない）。`Fallback`: `"ゲストに{g}さんを迎え"` / `"{a}さんを特集し"` を `、` 連結 + `"ました。"`。素材無しは `""`。

---

## 3. 永続化（Infra 新規 `Persistence/YamlJournalStore.cs`）

`IJournalStore` 実装。**Mac `JournalStore.swift` 逐語移植**:
- DTO: `FileDto { WeekKey; Entries: List<EntryDto>? }` / `EntryDto { Date; Highlight }`（YAML キーは `week_key`/`entries`/`date`/`highlight`＝snake_case）。
- `Load()`: ファイル無し→`StationJournal.Empty`。あれば `YamlConfigLoader.Deserialize<FileDto>` → map。**壊れ（schema 不一致）は YamlDotNet の例外がそのまま throw**
  （呼び出し側が握り潰す＝事故ゼロ系。Mac は decode で throw し engine が `try?`）。空ファイル等で DTO が null なら `ConfigException.MissingField`（同じく caller が握り潰す）。
- `Save(journal)`: DTO 化 → `YamlDotNet` の `SerializerBuilder().WithNamingConvention(Underscored)` で直列化 → **原子的上書き**（`ArtistListGenerator.Write`
  と同 idiom・Mac `atomically:true` の確立済み移植）: 親ディレクトリ作成 → 一意 temp（`<file>.{guid}.tmp`）に `UTF8Encoding(false)`（BOM なし）で書き →
  **既存なら `File.Replace(temp, path, destinationBackupFileName: null)`／初回は `File.Move(temp, path)`**、失敗時は `try { File.Delete(temp); } catch {}` してから rethrow
  （生 `File.Move(overwrite:true)` は使わない＝置換意味論が弱く、失敗時に config/ へ temp を残しうる）。
- 配置: `config/journal.local.yaml`（Mac 同一・gitignore 済み）。

---

## 4. 注入経路（DialogueScriptGenerator / CornerContext / CornerEngine）

- **`DialogueScriptGenerator.MakeRequest`**（`DialogueScriptGenerator.cs`）: 引数末尾の `guest` の後・`temperature` の前に `string journalContext = ""` を追加（Mac 引数順）。
  - `dateContext` セクションの後に **journalContext セクション**を追加: `if (journalContext.Length > 0) sections.Add($"# 前回までの番組の振り返り\n{journalContext}");`（Mac `DialogueScriptGenerator.swift:92-98` 逐語）。
  - **greeting 非 null の分岐内**（冒頭コーナー）に、時刻断定抑制の制約の**後**に振り返り制約を追加: `if (journalContext.Length > 0) constraints.Add("冒頭で、前回までの放送の振り返り（上記）に軽く一言だけ触れてから本題へ入る（長々と振り返らない）。");`（Mac `:116-118` 逐語）。
  - **内部呼び出しの修正必須**: `GenerateAsync` 内の `MakeRequest(corner, djs, song, theme, dateContext, letter, greeting, guest, _temperature)` は positional で `_temperature` を渡しているため、`journalContext` 挿入で引数がずれる。`MakeRequest(..., guest, journalContext, _temperature)` に**明示修正**する。
- **`GenerateAsync`**: 引数末尾の `guest` の後・`ct` の前に `string journalContext = ""` を追加。`MakeRequest` へ素通し。
- **`CornerContext`**（`Dialogue.cs`）: 末尾に `string? JournalContext = null` を追加（Mac `CornerContext(... , journalContext)`）。
- **`CornerEngine.PrepareAsync`**: generator 呼び出しに `journalContext: context.JournalContext ?? ""` を追加（他は無改変。letter/guest/dateContext と同じ素通し）。

> **既存テスト非破壊**: `DialogueScriptGeneratorTests`/`CornerEngine` の `MakeRequest`/`GenerateAsync` 呼び出しは**すべて named 引数**（temperature を positional 9 番目に渡す箇所は内部の 1 箇所のみ＝上記で明示修正）。journalContext は末尾 optional ゆえ他は無改変。

---

## 5. 番組エンジン配線（BroadcastEngine）

- **ctor**: 末尾（`artistFeatureRunner` の後）に `IJournalStore? journalStore = null, JournalSummarizer? journalSummarizer = null` を追加（**末尾 optional＝既存 positional 呼び出し非破壊**。`randomIndex`/`artistFeatureRunner` と同方針）。フィールド `_journalStore`/`_journalSummarizer` を保持。
- **開始時の読み込み**（`BroadcastAsync`、greeting 計算の直後）:
  ```csharp
  // 前回までの振り返り（当週のハイライトを冒頭コーナーの準備に渡す。読み込み失敗は無視＝fail-tolerant。W18 §6）。
  var journalContext = JournalContextFromCurrentWeek();   // store null / load 失敗 / 空 → ""
  ```
  - `JournalContextFromCurrentWeek()`: `_journalStore is null → ""`。`try { Load() } catch { return ""; }`（壊れ/IO 失敗を握り潰す）。`EntriesForCurrentWeek(_clock.Now, _timeZone)` → `JournalContext(entries)`。
  - `static string JournalContext(entries)`: 直近 **3 件**（`entries.Count<=3 ? entries : 末尾3`）を `・{Highlight}` で `\n` 連結。空なら `""`（Mac `journalContext(from:)` 逐語＝`suffix(3)`）。
- **冒頭コーナーにのみ注入**: `StartPreparation` に `string journalContext` 引数を追加（`greeting` の隣）。talk の非ゲスト分岐の `CornerContext` 構築に `JournalContext: index == firstTalkIndex ? journalContext : null` を追加。**ゲスト分岐・特集・他コーナーには付けない**（Mac `cornerContext(...)`: guest 分岐が先・journalContext は firstTalkIndex のみ）。`EnsurePrepared` が `journalContext` を渡す。
- **正常終了時のみ保存**（`BroadcastAsync` の正常完走パス、`PauseAsync` の後・`BroadcastFinished` 発火の**前**。Mac `BroadcastEngine.swift:254-257` 順）:
  ```csharp
  await SaveJournalAsync(guest, artist, ct).ConfigureAwait(false);
  _onEvent?.Invoke(new BroadcastEvent.BroadcastFinished());
  ```
  - `SaveJournalAsync(guest, artist, ct)`: `_journalStore is null || _journalSummarizer is null → return`。`digest = new BroadcastDigest(DateString(_clock.Now,_timeZone), guest?.Name, artist?.Name)`。`!digest.HasContent → return`（**ゲストも特集も無い回は記録しない**＝確定 A）。`highlight = await _journalSummarizer.SummarizeAsync(digest, ct)`。`highlight.Length==0 → return`。`current = try Load() catch Empty`。`updated = current.Appended(new JournalEntry(digest.Date, highlight), _clock.Now, _timeZone)`。`try Save(updated) catch {}`（保存失敗は握り潰す）。
  - `static string DateString(now, tz)`: `local = ConvertTime(now,tz).DateTime` → `$"{Year:D4}-{Month:D2}-{Day:D2}"`（Mac `dateString` 逐語）。
  - **停止/キャンセルでは到達しない**: ループの `catch { Pause; throw; }` で抜けるため `SaveJournalAsync` は正常完走パスのみ実行（Mac 一致）。

---

## 6. 注入の意味（プロンプト）

- 当週ハイライトを `・<highlight>` で連結した `journalContext` 文字列を作り、**冒頭コーナーのみ**に注入。
- 冒頭プロンプトに `# 前回までの番組の振り返り` セクション ＋「軽く一言だけ触れてから本題へ入る（長々と振り返らない）」制約が入る。
- 当週エントリが無い（週初・初回・ファイル無し・週替わり）ときは `journalContext == ""` ＝注入なし（従来どおり）。

---

## 7. 完全静寂・事故ゼロとの整合

- **保存は正常終了時のみ**（`BroadcastFinished` 経路）。停止（メニュー停止）・キャンセルでは保存しない（中断した回はハイライトにしない＝完全静寂中に I/O を増やさない・CLAUDE.md §3-1）。
- 長期記憶は**完全 fail-tolerant**: 要約 LLM 失敗・ファイル破損・保存失敗のいずれも**放送を止めない**（握り潰し）。**throw する専用コードは設けない**（`error-codes.md` の JNL 方針＝W18 で記載済み）。

---

## 8. テスト（`dotnet test AIRadio.slnx` 緑が完了条件）

- **`StationJournalTests`（Core 新規）**（固定日付・tz 固定で決定論）:
  - (a) **ISO 週キー（月曜始まり）**: 土・日が同週、翌月曜で替わる（2026-06-13 土 / 06-14 日 / 06-15 月）。
  - (b) `EntriesForCurrentWeek`: 当週一致で entries / 翌週で空。
  - (c) `Appended`: 週替わりで過去を捨ててから追記（月曜クリア）。
  - (d) `Appended`: 同週追記・`MaxEntries`(7) 超で古い順に落とす（リングバッファ・先頭が落ちる）。
  - (e) **tz 依存**（Win 固有実装の確認）: 同一インスタント（日曜 23:30 UTC）が UTC では当週・`+9` tz では翌週キー＝`WeekKeyFor` の tz 解釈（`ConvertTime`）を検証。
- **`JournalSummarizerTests`（Core 新規）**: LLM 応答を記録文に（前後空白除去）／ LLM 失敗（空 `ScriptedLLM`）で決定論フォールバック（ゲスト・特集が文に）／ 素材無しでフォールバック空（`HasContent` も検証）。
- **`JournalStoreTests`（Infra 新規）**: `Save`→`Load` 往復（WeekKey ＋ entries 列を個別検証）／ ファイル無し＝`Empty`／ 壊れ yaml は `Load` が throw。
- **`BroadcastEngineJournalTests`（Core 新規。具象 `CornerEngine` ＋ 内容ルーティング LLM を注入）**:
  - 正常終了でゲストの回はハイライトが保存される。**`JournalSummarizer` は talk の内容ルーティング LLM とは別個の `JournalSummarizer(new ScriptedLLM(summaryResponse))` で構築して engine へ注入**（Mac `makeEngine` 同様、要約応答を決定論化＝ルーティング LLM へ summarizer プロンプトが落ちて `letter` 応答にならないようにする）。検証は `SaveCount==1` かつ `store.Journal.Entries[^1].Highlight == summaryResponse`。
  - 当週の振り返りが**冒頭コーナーの台本プロンプト**に入り、**途中コーナーには入らない**（冒頭＝「番組の最初のコーナー」を含む台本リクエストにハイライトが入り、「番組の途中のコーナー」を含むものには入らない）。**漏洩ガード**（Win prompt レベルで Mac の type レベル保証に揃える）: 記録した全 `LLMRequest` のうち**ハイライト文字列を含むのは「番組の最初のコーナー」を含むリクエストだけ**（`Count(含む) == Count(冒頭マーカー含む)` ＋ ハイライトを含む各リクエストは冒頭マーカーも含む）＝選曲候補・お便り生成・ニュース等へ漏れない。
  - **キャンセルされた回は保存しない**（保存対象のゲスト在席でも `SaveCount==0`・`BroadcastFinished` 不発）。
  - **ゲストも特集も無い回は保存しない**（`HasContent==false` → `SaveCount==0`、Win 追加カバレッジ）。
  - **壊れジャーナルを engine が握り潰す**（spec の事故ゼロ保証の検証。`ThrowingJournalStore`〔Load が必ず throw〕注入）: ① 開始時 Load が throw でも空振り返りで完走（`BroadcastFinished` 発火・振り返りセクション非注入）／ ② 保存時 Load が throw でも Empty から積み直して完走（`SaveCount==1`・1 エントリ保存）。`JournalContextFromCurrentWeek`/`SaveJournalAsync` の 2 つの catch サイトを実証（Win 追加カバレッジ）。
- **`DialogueScriptGeneratorTests`（拡張）**: `journalContext` 非空 ＋ greeting で `# 前回までの番組の振り返り` セクション ＋「軽く一言だけ触れて」制約が入る／ 空なら入らない／ greeting null（途中）では振り返り制約を出さない。
- **回帰（不変）**: 完全静寂・ローリング準備・ED で終了・時報の時刻正確性・W12〜W17・`BroadcastSession`。journalStore/summarizer 未注入（既定 null）のテストは保存・注入なしで従来どおり緑。

---

## 9. 影響ファイル一覧

| 層 | ファイル | 変更 |
|---|---|---|
| Core | `StationJournal.cs`（新規）| `JournalEntry`/`StationJournal`/`BroadcastDigest`/`JournalSummarizer` |
| Core | `Abstractions.cs` | `IJournalStore` 追加（design §2 シーム）|
| Core | `Dialogue.cs` | `CornerContext` に `JournalContext` 追加 |
| Core | `DialogueScriptGenerator.cs` | `MakeRequest`/`GenerateAsync` に `journalContext`・セクション＋冒頭制約・内部呼び出し明示修正 |
| Core | `CornerEngine.cs` | generator へ `journalContext: context.JournalContext ?? ""` を渡す |
| Core | `BroadcastEngine.cs` | ctor 末尾に `journalStore`/`journalSummarizer`、開始時 load→冒頭注入、正常終了で save（`JournalContextFromCurrentWeek`/`JournalContext`/`SaveJournalAsync`/`DateString`）|
| Infra | `Persistence/YamlJournalStore.cs`（新規）| `journal.local.yaml` ⇄ `StationJournal`（原子的上書き・load 壊れは throw）|
| App | `Composition/BroadcastComposition.cs` | `YamlJournalStore` ＋ `JournalSummarizer(llm)` を engine へ注入 |
| TestSupport | `Fakes.cs` | `InMemoryJournalStore`（`IJournalStore`・SaveCount/Journal 記録）|
| docs | `design.md` | W18 行を実装済みに（必要なら注記）|
| Core.Tests | `StationJournalTests.cs`（新規）| §8 |
| Core.Tests | `BroadcastEngineJournalTests.cs`（新規）| §8 |
| Core.Tests | `DialogueScriptGeneratorTests.cs` | journalContext 注入テスト追加 |
| Infra.Tests | `JournalStoreTests.cs`（新規）| §8 |

- 新規エラーコードなし（JNL は throw しない・記載済み）。**新 interface は `IJournalStore` のみ**（design §2 の正規シーム）。

---

## 10. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。新規 throw コードなし。新 interface は `IJournalStore` のみ。
- **実機確認（要 VOICEVOX + Spotify Premium・最後に一括）**:
  1. 1 回放送（ゲストか特集ありで ED まで完走）→ 終了後に `config/journal.local.yaml` が作られ、その回のハイライト要約が入る。
  2. 同じ週にもう 1 回放送 → **冒頭コーナー**で前回の振り返り（ゲスト・特集など）に**軽く一言**触れる（途中コーナーでは触れない）。
  3. `journal.local.yaml` を削除 → 次回は振り返りなし（クリア）。
  4. 停止で終えた回・ゲストも特集も無い回は記録されない。既存進行・完全静寂・W12〜W17 に影響なし。
  5. （週境界）weekKey をまたぐと前週分が注入されない（テストで担保。実放送は週替わりで確認できれば）。

---

## 11. 確定設計判断（W18）／ Win 固有 parity 注記

1. **確定 A–E（Mac s18 §12・2026-06-14 ユーザー承認）を踏襲**: A=ハイライトは日付・ゲスト・特集のみ（テーマ含めない）・軽く一言 / B=注入先は冒頭コーナーのみ / C=ISO 週（月曜始まり）・月曜クリア / D=ED で正常終了した回のみ記録 / E=`config/journal.local.yaml`（gitignore・人為削除でクリア）。
2. **ISO 週キーは `System.Globalization.ISOWeek`**（`GetYear`/`GetWeekOfYear`・`ConvertTime` で tz 解釈）。Mac の `Calendar(.iso8601)` と同結果（ISO 8601 月曜始まり）。**本スライス唯一の Win 固有実装差分**。
3. **`IJournalStore` は新 interface を作ってよいケース**（design.md §2 の正規シーム＝load/save の差し替え境界・テストで `InMemoryJournalStore` 注入。`YamlJournalStore` は唯一の本番実装）。`JournalSummarizer`/`StationJournal`/`BroadcastDigest` は**具象**（§3-5）。
4. **完全 fail-tolerant・throw コードなし**: 要約 LLM 失敗（OCE 含む全例外を握り潰し→Fallback）・load 壊れ（engine が try/catch→Empty）・save 失敗（try/catch で無視）。`error-codes.md` の JNL 方針どおり新コードを足さない。
5. **保存は正常完走パスのみ**（`BroadcastFinished` 直前）。停止/キャンセルはループの catch で抜けて到達しない（Mac 一致）。`ct` は正常パスでは未キャンセルだが、要約は fail-tolerant ゆえ万一のキャンセルでも Fallback で保存できる。
6. **ctor は末尾 optional 追加**（既存 positional 呼び出し非破壊。Mac は引数順が異なるが named/optional ゆえ無害）。`journalStore`/`journalSummarizer` 未注入（既定 null）なら従来挙動（保存・注入なし）。
7. **値等価**: `StationJournal` の equality はオーバーライドせず、テストは `WeekKey` と `Entries`（要素値等価の `JournalEntry` 列）を個別検証（whole-object `==` 非依存）。
8. **journalContext は dateContext と同型の素通し**（`MakeRequest` の section 追加 ＋ 冒頭 greeting 分岐の制約 1 本）。注入の制御（冒頭のみ・週リセット）は **BroadcastEngine 側**に集約（generator は文字列を受けるだけ）。

Mac リファレンス: `StationJournal.swift`（モデル・要約の正）/ `JournalStore.swift`（永続化の正）/ `BroadcastEngine.swift:42-46,182-186,254-282,486-516`（load/注入/save の正）/ `DialogueScriptGenerator.swift:24,49,92-98,116-118`（注入経路）/ `StationJournalTests.swift`・`JournalStoreTests.swift`・`BroadcastEngineJournalTests.swift`（テストの正）。SoT: `docs/specs/s18-station-journal.md`。
