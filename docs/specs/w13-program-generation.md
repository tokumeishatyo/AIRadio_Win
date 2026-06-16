# W13 — コーナー数 N 駆動の番組生成 + 「ED で終了」 + 番組長 UI

Mac 版 `s13`（`../AIRadio_Mac-main/docs/specs/s13-program-generation.md`）の Windows 移植。
番組を**固定セグメント列（v1）から、コーナー数 N で決定論的に生成する方式（v2 部品宣言）へ進化**させ、
長時間放送を可能にする。放送中に**「ED で終了」**で優雅に締められるようにし、
**番組の長さ**をトレイメニューから選んで永続化する。

> **スコープ確定（2026-06-16, ユーザー決定）**: **エンドレスはトレイメニューに出さない**（トーク 10/20/30 本は
> 十分に長いため不要）。`ProgramLength.Endless` / `ProgramPlan` のエンドレス分岐は **Core に Mac 忠実移植として
> 残し単体テストも維持**するが、**UI からは選べない**（finite のみ提供）。これに伴い `∞` 進捗表示は不要となり、
> 放送中の進捗カウンタ `(5/23)` も **defer**（UI 配線を最小化。状態表示は「放送中…」のまま）。

> 進め方は確立パターン（CLAUDE.md §7）に従う: 本 spec 凍結 → ユーザー承認 → 実装 → 多角レビュー
> Workflow → `dotnet test` 緑 → commit/push → Confluence ミラー。

---

## 0. スコープ（W13 で入れるもの / 入れないもの）

### 入れる（3 本柱、Mac s13 §2〜§6）
1. **N 駆動の番組生成** — `ProgramBlueprint`（部品宣言）+ `ProgramPlan`（決定論的セグメント生成）。
   `program.yaml` を v1（明示 `segments:` 列）から v2（部品宣言）へ置換し、`ProgramFormat` を廃止する。
2. **「ED で終了」** — `BroadcastControl` で放送中に ED 要求。現セグメント（+ 準備済みの直後トーク）を
   流したら残りを飛ばして ED で締める。finite の放送を途中で早く締めるのに使う。
3. **番組長 UI** — トレイの `NativeMenu` サブメニュー「番組の長さ」（**トーク 10/20/30 本**）。
   選択を `%LOCALAPPDATA%` に永続化し再起動後も保持。既定値は `program.yaml` の `default_length`。

### Core のみ（Mac 忠実・UI 非露出）
- **エンドレス連続放送** — `ProgramLength.Endless` / `ProgramPlan` の `talk, talk, letter, news` 無限ループ・
  ED 合成は Core に**移植し単体テストも維持**するが（Mac パリティ・「ED で終了」の合成 ED ロジックと共有）、
  **トレイメニューには出さない**（ユーザー決定 2026-06-16）。将来 UI 露出は数行で可能。

### 入れない（後続スライスへ明示 defer）
- **曜日替わりメイン DJ・コーナー頭二層化（`WeeklyCast` / `DjSpiel` / `ThemedSegment.by_dj` / `lead_in` / 冒頭挨拶）** → **W13.5**。
  本 W13 では OP/ED/news は従来どおり `anchor_dj_id` が読み、`BroadcastThemes` は現行のフラット形のまま。
- **ゲストコーナー（`guest_corner_id` / `guests.yaml`）** → **W14**。
- **アーティスト特集（`artist_feature_corner_id` / `ArtistFeature*` / `SegmentKind.ArtistFeature`）** → **W15**。
- **長期記憶ジャーナル（`JournalStore` / `JournalSummarizer`）** → **W18**。
- **読み辞書同期（`VoicevoxUserDict`）** → **W19a**。

> Mac の `Program.swift`/`BroadcastEngine.swift`/`MenuBar.swift` は上記後続機能が既に蓄積されている。
> W13 は **s13 spec §2〜§6 の素のコア**だけを移植し、後続フィールド・分岐は持ち込まない（§3-5）。

---

## 1. 番組生成規則（`ProgramPlan`、決定論的）

- **N = トークコーナー（`free_talk`）の本数**。お便り / ニュース / OP / 冒頭曲 / ED は数えない。
- 構成: `opening → song（冒頭曲）→ 本編 → ending`
  - 本編: トーク 2 本ごとに「お便り → ニュース」を自動挿入（= `talk, talk, letter, news` の繰り返し）。
  - **端数処理**: N が奇数のとき、最後の 1 本のトークの後はお便り・ニュースを挟まず ED へ。
  - **エンドレス**: `talk, talk, letter, news` を無限に繰り返す。**ED セグメントなし**。
- 例:
  - N=4 → `OP, song, t1, t2, letter, news, t3, t4, letter, news, ED`（11 セグメント）
  - N=3 → `OP, song, t1, t2, letter, news, t3, ED`（8 セグメント）
  - N=1 → `OP, song, t1, ED`（4 セグメント）
  - N=2 → `OP, song, t1, t2, letter, news, ED`（7 セグメント）
  - endless → `OP, song, t1, t2, letter, news, t1, t2, letter, news, …`（ED なし、無限）

---

## 2. Core 型（`AIRadio.Core/Program.cs` に追加）

### 2-1. `ProgramLength`（番組の長さ）

Mac は associated value 付き enum（`.corners(Int)` / `.endless`）。C# には連想値 enum が無いため、
**値等価な `readonly record struct`** で表現する（新クラス・interface を作らない、§3-5）。

```csharp
public readonly record struct ProgramLength
{
    public bool IsEndless { get; }
    public int Corners { get; }   // IsEndless のときは未使用（0）

    private ProgramLength(bool isEndless, int corners) { ... }

    public static ProgramLength FromCorners(int count);   // count >= 0
    public static readonly ProgramLength Endless;

    /// UserDefaults 相当の永続文字列（"10" / "endless"）。
    public string RawValue { get; }
    /// "endless" または 0 以上の整数のみ受ける（不正は false）。
    public static bool TryParse(string? raw, out ProgramLength length);
}
```

- `RawValue`: `corners(n)` → `n.ToString()`、`endless` → `"endless"`（Mac `rawValue` 一致）。
- `TryParse`: `"endless"` → endless、整数（≥0）→ corners、それ以外 → false（Mac `init?(rawValue:)` 一致）。

### 2-2. `ProgramBlueprint`（`program.yaml` v2 の中身）

s13 §2 の素のフィールドのみ（weeklyCast/guest/artistFeature は持たない＝後続スライス）。

```csharp
public sealed record ProgramBlueprint(
    string Title,
    string AnchorDjId,          // OP / news / ED を読む DJ
    ProgramLength DefaultLength, // メニューの既定値
    bool OpeningCritical,        // OP 失敗で放送中止か（既定 true、Windows 踏襲）
    SongSegmentSpec Song,        // 冒頭曲
    string TalkCornerId,         // free_talk。N はこのコーナーの本数
    string LetterCornerId,       // お便り
    string? NewsDjId);           // ニュース専任読み手（null なら anchor）
```

### 2-3. `ProgramPlan`（N からセグメントを決定論的に生成）

```csharp
public sealed class ProgramPlan
{
    public ProgramBlueprint Blueprint { get; }
    public ProgramLength Length { get; }
    public string Title => Blueprint.Title;
    public string AnchorDjId => Blueprint.AnchorDjId;

    /// 総セグメント数（エンドレスは null。UI の「n/全体」表示用）。
    /// 有限: OP + song + 本編(ペア×4 + 端数) + ED = 2 + (N/2)*4 + (N%2) + 1
    public int? TotalSegmentCount { get; }

    /// index 番目のセグメント（0 始まり）。有限は ED の次で null、エンドレスは常に非 null。
    public ProgramSegment? Segment(int index);
}
```

生成ロジック（Mac `ProgramPlan.segment(at:)` / `patternBodySegment` を移植。ゲスト・特集の差し込みは無し）:
- `index < 0` → null
- `index == 0` → `Opening`（`Critical = Blueprint.OpeningCritical`）
- `index == 1` → `Song`（`Blueprint.Song`）
- `index >= 2` → 本編 body（`body = index - 2`）:
  - endless: `pattern(body % 4)`
  - corners(N): `pairBody = (N/2)*4`, `remainder = N%2`
    - `body < pairBody` → `pattern(body % 4)`
    - `remainder == 1 && body == pairBody` → `Talk(TalkCornerId)`（端数トーク）
    - `body == pairBody + remainder` → `Ending`
    - それ以外 → null
  - `pattern(pos)`: `0/1` → `Talk(TalkCornerId)`、`2` → `Talk(LetterCornerId)`、`3` → `News(NewsDjId)`

### 2-4. `BroadcastControl`（放送中の操作ハンドル）

UI スレッドから安全に呼べる lock ガード（Mac `BroadcastControl` の `NSLock` → C# `lock`）。

```csharp
public sealed class BroadcastControl
{
    public void RequestEnding();      // 「ED で終了」要求
    public bool IsEndingRequested { get; }
}
```

### 2-5. `ProgramFormat` の廃止

`ProgramFormat` record は削除し、`ProgramBlueprint` + `ProgramPlan` に置換する。`SegmentKind` / `ProgramSegment` /
`SongSegmentSpec` / `BroadcastThemes` は現状維持（`SegmentKind.ArtistFeature` は W15 まで追加しない）。

---

## 3. `BroadcastEngine` の進化（`AIRadio.Core/BroadcastEngine.cs`）

W7 でローリング先読み準備（窓 W=2）・news 出現毎生成・完全静寂・fail-tolerant/critical は実装済み。
W13 の変更点は **(a) plan 駆動の無限ループ化** と **(b) 「ED で終了」** の 2 点。

### 3-1. RunAsync シグネチャ

```csharp
public async Task RunAsync(
    ProgramPlan plan,                  // ← ProgramFormat format から変更
    BroadcastThemes themes,
    IReadOnlyList<CornerTemplate> corners,
    IReadOnlyList<DjProfile> djs,
    BroadcastControl? control = null,  // ← 追加（null = ED で終了なし）
    CancellationToken ct = default)
```

- **preflight（音を出す前、fail-fast）**: `plan.AnchorDjId` の DJ 実在、`Blueprint.TalkCornerId` /
  `Blueprint.LetterCornerId` の corner 実在、`Blueprint.NewsDjId`（非 null 時）の DJ 実在を検証
  （`ConfigException` / `E-CFG-MISSING-FIELD-001`）。セグメント列ではなく blueprint フィールドを検証する（Mac 一致）。

### 3-2. 進行ループ（有限／エンドレス共通）

```
EnsurePrepared(PreparationWindow);     // 冒頭曲確定 → {first_song}
... {first_song} 構築（plan.Segment(1) が Song なら曲振りに）...
int index = 0;
while (plan.Segment(index) is ProgramSegment segment)
{
    ct.ThrowIfCancellationRequested();
    EnsurePrepared(index + PreparationWindow);
    await RunSegmentAsync(index, segment, ...);
    ledger.Discard(index);
    // ★「ED で終了」判定（§3-4）
    if (control is { IsEndingRequested: true } && segment.Kind != SegmentKind.Ending) { ... break; }
    index++;
}
```

> **単一 prepCts / prepCt パラメータは廃止**する（現行 `BroadcastEngine.cs:111` の
> `using var prepCts = CreateLinkedTokenSource(ct)` と、それを `BroadcastAsync`/`StartPreparation` に
> 引き回す `prepCt` 引数）。各準備 Task は `ledger.BeginPreparation(index, ct)` が返す **per-index token** で
> 起動し、停止後始末は finally の `CancelAll() + DrainAsync()` に一本化する（§3-3）。

- 有限は `Segment` が ED の次で null を返してループ終了。エンドレスは null を返さない＝
  「放送を停止」（ct cancel）か「ED で終了」でのみ抜ける。
- `{first_song}` は従来どおり OP 前に冒頭曲（index 1）の選曲完了を待つ（開始前無音は許容、放送中無音ゼロ不変）。

### 3-3. ローリング準備台帳（`PreparationLedger`）の改修 — セグメント別 CTS

現行は単一の `prepCts`（`CreateLinkedTokenSource(ct)`）を全準備 Task で共有しているため、
**「ED で終了」で 1 本だけ残して他をキャンセル」ができない**。Mac は準備 Task ごとに独立した
`Task.cancel()` を持つ。これを **セグメント index ごとの linked CTS** で再現する。

- `BeginPreparation(index, ct)` → `CreateLinkedTokenSource(ct)` を index に紐付けて token を返す。
  `StartPreparation` は各種別の準備をこの token で起動する（停止＝エンジン ct で全 index 連動キャンセル）。
- `MarkCornerPrepared(index)` / `IsCornerPrepared(index)` を追加（ED 判定で「直後トークが**準備完了済み**か」を見る。
  Mac `preparedCorners` 移植）。**マークは `PrepareAsync` が成功完了した後のみ**行う（Mac の
  `BroadcastEngine.swift:426-430` = `await runner.prepare(...)` 解決後にマーク、を忠実移植）。実装は
  トーク準備を async ラッパで包む: `ledger.AddCorner(index, PrepareCornerAsync(index, corner, djs, token))` /
  `PrepareCornerAsync = { var p = await _cornerRunner.PrepareAsync(corner, djs, token); ledger.MarkCornerPrepared(index); return p; }`。
  **キャンセル・失敗時はマークしない**（未完了/faulted な Task を「準備済み」と誤判定すると ED で待ち時間/例外が出る）。
- `CancelAll(int? keeping = null)` → `keeping` 以外の **生存（未除去）** index の CTS を Cancel（Mac `cancelAll(keeping:)`）。
  冪等（既に Cancel 済みでも no-op）。
- `Discard(index)` → その index の Task と CTS の **エントリを lock 下で先に辞書から除去してから** Dispose する
  （後続の `CancelAll`/`Discard`/finally が **Dispose 済み CTS を Cancel して `ObjectDisposedException`** にならないため）。
- `DrainAsync()` → 残存 Task を **await して観測（例外を握り潰す）した後にその index の CTS を Dispose** する
  （token を観測中の in-flight Task がいる CTS を先に Dispose しない＝順序が load-bearing）。
- RunAsync の `finally` は単一 prepCts.Cancel の代わりに `ledger.CancelAll(); await ledger.DrainAsync();`。
  `CancelAll()` は生存エントリのみを対象とするため、ED 分岐で `Discard` 済みの index に触れない。

### 3-4. 「ED で終了」（graceful ending）

各セグメント完了時に判定（Mac `BroadcastEngine.broadcast()` の ED ブロック移植、artistFeature 分岐は除外）:

1. `control.IsEndingRequested` かつ現セグメントが ED でない → `BroadcastEvent.EndingRequested` を発火。
2. `plan.Segment(edIndex)`（`edIndex = index + 1`）が **非 null かつ `Talk` かつ
   `CornerId == Blueprint.TalkCornerId`（= free_talk、お便りでない）かつ準備完了済み（`IsCornerPrepared`）** なら、
   `CancelAll(keeping: edIndex)` で窓の外を捨ててからそれを 1 本流し、`Discard(edIndex)`、`edIndex++`。
   - Mac `BroadcastEngine.swift:230-233` の `let next = plan.segment(at: edIndex)`（非 nil 先頭）に合わせ、
     C# は null 安全なパターンで書く: `if (plan.Segment(edIndex) is { Kind: SegmentKind.Talk, CornerId: var cid } && cid == blueprint.TalkCornerId && ledger.IsCornerPrepared(edIndex))`
     （`plan.Segment(edIndex).Kind` の直参照は NRE になるため不可）。
3. それ以外（お便り / ニュース / 未準備トーク / song）は `CancelAll()` で全破棄。
4. 合成 `ProgramSegment(SegmentKind.Ending)` を `edIndex` で実行して `break`
   （エンドレスは plan に ED が無いため合成、有限も前倒しで同じ）。
- エンドレスでも有効（ED が唯一の優雅な終わり方）。有限の ED 実行後は通常終了と同じ。

### 3-5. イベント追加

`BroadcastEvent.EndingRequested`（引数なし record）を追加。コンソール表示は
「ED で終了を受け付けました（残りのコーナーを飛ばして ED へ）」。

---

## 4. config v2（`config/program.yaml`、セグメント列 → 部品宣言）

```yaml
program:
  title: "ケイラボAIラジオ"
  anchor_dj_id: zundamon
  default_length: 10            # 1 以上の整数 / "endless"
  opening:
    critical: true             # OP 失敗は放送中止（既定 true）
  song:                        # 冒頭曲（OP 直後の本日の1曲目）
    song_prompt_hint: "一日の始まりや番組の幕開けに合う、前向きで広く知られた曲"
    fallback_track_uri: "spotify:track:5jsqaNOAbeBG5QYL7JpySJ"
    volume: 100
    play_seconds: 0
  talk:
    corner_id: free_talk
  letter:
    corner_id: letter
  news:
    dj_id: ryusei
```

- 旧形式（`segments:` 列挙）は**廃止**。部品の差し替え・既定長は YAML で変更可（設定駆動の精神は維持）。
- 後続スライス用キー（`weekly_cast` / `guest` / `artist_feature`）は **v2 には書かない**（W13.5/W14/W15 で追加）。

### `ProgramConfig`（`AIRadio.Infrastructure/Config/ProgramConfig.cs`）を v2 ローダへ置換

- `FromYaml` / `LoadFile` は `ProgramBlueprint` を返す（`ProgramFormat` を返さない）。
- 必須: `anchor_dj_id` / `song.fallback_track_uri` / `talk.corner_id` / `letter.corner_id`
  （欠落は `ConfigException.MissingField` ＝ `E-CFG-MISSING-FIELD-001`）。任意: `news.dj_id`。
- `title` 既定 = `"ケイラボAIラジオ"`、`opening.critical` 既定 = `true`、`song.volume/play_seconds` 既定 = `100/0`。
- `fallback_track_uri` は `SpotifyUri.NormalizeTrack` で正規化（共有 URL / 裸 ID 対応、W7 同様）。
- `default_length`:
  - DTO は `string?` で受ける。YamlDotNet はスカラノードを string プロパティへそのまま束縛する
    （`10` も `"10"` も `endless` も可。mapping/sequence は型不一致で fail-fast）。
    **検証済み: YamlDotNet 18.0.0 + `YamlConfigLoader` の Deserializer**（`UnderscoredNamingConvention` +
    `IgnoreUnmatchedProperties`）で `10`/`"10"`/`endless`/`-3`/`0`/`yes`/`10.5` はすべて string に束縛・throw なし、
    欠落は null。よって不正判定は `ProgramLength.TryParse` に一本化できる（カスタムコンバータ不要）。
  - `ProgramLength.TryParse` で解釈し、**corners は 1 以上**を要求（`< 1` や不正は fail-fast、Mac 一致）。
  - **欠落（key 無し = null）のみ既定 `corners(10)` に倒す**。`raw is null` で判定する（`IsNullOrEmpty` は不可）。
    **明示的な空文字 `""` は欠落と区別し fail-fast**（`TryParse("")` は false。Mac の `ProgramLength(rawValue:"")==nil` 一致）。

---

## 5. 番組長の永続化（`%LOCALAPPDATA%`）

Mac は `UserDefaults`。Windows に同等が無いため、`%LOCALAPPDATA%\AIRadio\` 配下に小さな設定ファイルで保持する
（`DpapiTokenStore` が `spotify.token` を置くのと同じディレクトリ。番組長は機密でないため平文）。

```csharp
// AIRadio.Infrastructure（Windows パス + ファイル IO はこの層）
public sealed class ProgramLengthStore
{
    public ProgramLength? Read();        // 不在 / 不正は null
    public void Write(ProgramLength length);
}
```

- 保存値は `ProgramLength.RawValue`（UI からは `"10"`/`"20"`/`"30"`。`"endless"` は型としては表現可だが UI は書かない）。
  ファイル名 `program-length`。
- 新 interface は作らない（§3-5。テスト可能な解析ロジックは Core 側 `ProgramLength.TryParse` に集約済み）。
- **選択の解決**（Mac `selectedProgramLength`）: `store.Read() ?? blueprint.DefaultLength`。
  - `BroadcastComposition`: plan 構築時にこの順で長さを決める。
  - `TrayController`: チェックマーク表示の現在値もこの順（既定は `program.yaml` ロード、失敗時 `corners(10)`）。
    既定が `endless`（config 由来）でメニューに該当項目が無い場合はどの項目もチェックしない（UI に endless は無いため）。

---

## 6. UI（`AIRadio.App/TrayMenu/TrayController.cs`）

メニュー構成（上から）: 状態表示 / 放送を開始⇄停止 / **ED で終了** / —— / **番組の長さ ▸** / —— / 終了。

### 6-1. 「ED で終了」
- `NativeMenuItem("ED で終了")`。放送開始時に `BroadcastControl` を生成して `_activeControl` に保持し、
  Click で `_activeControl?.RequestEnding()`。**Click 時に状態ラベルを `放送中…（ED で終了します）` へ楽観的に更新**する
  （Mac `requestEnding()` 同様。エンジンの `EndingRequested` イベント配線に依存しない＝UI 配線を増やさない）。
- **IsEnabled と `_activeControl` の生存は、既存の唯一の状態コールバック `TrayController.Apply(BroadcastSession.State)`
  から駆動する**（`TrayController.cs:85-98` を拡張）。Broadcasting → `IsEnabled = true`、Idle → `IsEnabled = false` かつ
  `_activeControl = null`。こうすることで、ユーザー停止だけでなく**自走で完走（BroadcastFinished → Finish → Idle）**した
  場合も ED 項目が確実に無効化される。`Apply` は `Dispatcher.UIThread.Post` 経由（UI スレッド）なので `IsEnabled` 操作の正しい場所。

### 6-2. サブメニュー「番組の長さ」
- 選択肢: `トーク 10 本 / トーク 20 本 / トーク 30 本`
  （`ProgramLength` = `corners(10/20/30)`。**エンドレスは出さない**。ラベルは Mac `programLengthLabel` 一致）。
- **サブメニュー構築（Avalonia API）**: 親 `NativeMenuItem` に子 `NativeMenu` を `Menu` プロパティで持たせる
  （現 `TrayController` はフラットメニューのみで前例なし）。
  ```csharp
  var lengthMenu = new NativeMenu();
  foreach (var choice in Choices)          // corners(10/20/30) のみ
      lengthMenu.Add(MakeLengthItem(choice));
  var lengthRoot = new NativeMenuItem("番組の長さ") { Menu = lengthMenu };
  menu.Add(lengthRoot);
  ```
- 現在値にチェック。選択で `ProgramLengthStore.Write` → チェック更新。**変更は次の放送開始から反映**
  （放送中の変更は次回分）。
- **NativeMenu の描画制約（design.md §9 / W9 の検証点）**: Avalonia の `NativeMenuItem` は
  `ToggleType = MenuItemToggleType.Radio` + `IsChecked` でラジオ選択を表す（Avalonia 12 の enum は
  `Avalonia.Controls.MenuItemToggleType`＝`None`/`CheckBox`/`Radio`）。Windows のトレイ
  `NativeMenu` で **(a) サブメニューのネストが描画されるか**、**(b) チェック（●）が描画されるか** はいずれも
  **実機確認が必要**（W9 検証はサブメニュー以前なので未カバー）。チェックが出ない場合のフォールバックとして、
  選択中項目のヘッダに先頭マーカー（例 `✓ トーク 10 本`）を付ける方式を用意する（実装は実機結果を見て確定）。

### 6-3. 放送中の進捗表示（W13 では defer）
- 進捗カウンタ `放送中: {kind} ({index+1}/{総数})`（Mac `apply(event:)`）は **本スライスでは実装しない**。
  状態表示は現行どおり `放送中…` / `停止中` のまま（「ED で終了」押下時のみ §6-1 で `放送中…（ED で終了します）`）。
  - 理由: エンドレスを UI に出さないため `∞` ケースが消え、可視確認の主目的が無くなった。カウンタは
    composition→tray のイベント転送（別スレッド発火 → `Dispatcher.UIThread.Post` マーシャリング）を要し配線が増える。
    後続で必要になれば、`BroadcastEngine` の `onEvent` を tray へ二股するだけで容易に追加できる（Core の
    `BroadcastEvent` に UI 専用ケースは足さない方針は維持）。
  - `ProgramPlan.TotalSegmentCount` は Core API として実装・テストする（将来のカウンタ表示・Mac パリティ用）。

### 6-4. `BroadcastComposition` の配線変更
- **新シグネチャ（`control` のみ追加・任意）**:
  ```csharp
  public async Task RunAsync(CancellationToken ct, BroadcastControl? control = null)
  ```
  これにより **DemoRunner（トレイ・control 無し）は `composition.RunAsync(cts.Token)` のまま**変更不要。
  TrayController は `composition.RunAsync(ct, control)` を渡す。進捗イベント転送（onEvent/onPlanReady）は §6-3 で defer。
- `ProgramConfig.LoadFile` → `ProgramBlueprint`。`ProgramLengthStore.Read() ?? blueprint.DefaultLength` で長さ決定。
  `new ProgramPlan(blueprint, length)` を構築。
- `newsDjId = blueprint.NewsDjId ?? blueprint.AnchorDjId`（従来はセグメントから取得）。
- `engine.RunAsync(plan, themes, corners, djs, control, ct)`。engine の `onEvent` は従来どおりログのみ。

---

## 7. 影響ファイル一覧

| 層 | ファイル | 変更 |
|---|---|---|
| Core | `Program.cs` | `ProgramLength` / `ProgramBlueprint` / `ProgramPlan` / `BroadcastControl` 追加、`ProgramFormat` 削除 |
| Core | `BroadcastEngine.cs` | plan 駆動ループ、ED 終了、`PreparationLedger` の per-index CTS 化、`BroadcastEvent.EndingRequested` |
| Infra | `Config/ProgramConfig.cs` | v2 ローダ（→ `ProgramBlueprint`）へ置換 |
| Infra | `ProgramLengthStore.cs` | 新規（`%LOCALAPPDATA%` 永続化） |
| App | `Composition/BroadcastComposition.cs` | plan 構築・長さ解決（store ?? default）・`control` 引数追加（onEvent/onPlanReady は defer）|
| App | `TrayMenu/TrayController.cs` | ED で終了（IsEnabled/活性 control を Apply で駆動・ラベル楽観更新）・番組長サブメニュー（10/20/30）|
| App | `DemoRunner.cs` | `broadcast` デモは **変更不要**（既定引数で `composition.RunAsync(cts.Token)` のまま。長さは blueprint 既定＝finite、endless 設定時は Ctrl-C で停止）|
| Core.Tests | `BroadcastEngineTests.cs` | `MakeFormat` → `MakeBlueprint`+`ProgramPlan`、全 `RunAsync` 呼び出しを plan(+control) シグネチャへ移行 + ED テスト追加 |
| Infra.Tests | `ProgramThemesConfigTests.cs` | v1 `segments` テスト（`f.Segments[...]`）を v2 部品宣言（`ProgramBlueprint`）テストへ全面置換 |
| config | `program.yaml` | v1 → v2 部品宣言へ書換 |
| docs | `design.md` | W13 行・§9 NativeMenu 検証点（チェックマーク + サブメニューネスト）の更新 |

---

## 8. テスト（`dotnet test AIRadio.slnx` 緑が完了条件）

### Core.Tests
- **`ProgramPlan`**: N=1/2/3/4/10 の全列挙（端数・ED 位置）、エンドレスの先頭 12 個 + `TotalSegmentCount == null`、
  有限の `TotalSegmentCount`（N=4→11 等）、ED の次で `Segment` が null。
- **`ProgramLength`**: `RawValue` / `TryParse`（"10"/"endless"/不正/負）/ 値等価。
- **`BroadcastEngine`（既存テストの plan 移行）**: 既存 `MakeFormat` を `MakeBlueprint`+`ProgramPlan`（N=1 で
  `OP→song→talk→ED`、news を含む形は N=2 等）に置換し、HappyPath / first_song 展開 / 時刻二段展開 /
  ED 無変化 / ローリング / 専任 news DJ / preflight fail-fast / fail-tolerant / critical OP / キャンセル静寂を維持。
- **ED で終了（新規）**: ①直後が準備済み free_talk → それを流して ED ②直後がお便り/ニュース → 即 ED
  ③直後トーク未準備 → 即 ED ④エンドレスでも ED で終了する（`EndingRequested` 発火、ED 後に終了、完全静寂）。
- **ローリング（新規/補強）**: エンドレスで準備 Task が際限なく積み上がらない（窓 k+2 以下）、
  news が出現回数ぶん生成、停止で保持中の準備がキャンセルされる。

### Infra.Tests（`ProgramThemesConfigTests` を v2 へ）
- 正常ロード（各フィールド）、`default_length` の各値（int `10` / 文字列 `"10"` / `"endless"` / **欠落（key 無し）→既定 10**）、
  **不正値 fail-fast（`0` / 負 / 非数: `"abc"` / `"10.5"` / 裸 `yes` / 空文字 `""`）**、
  必須欠落（anchor / song.fallback / talk.corner_id / letter.corner_id）fail-fast、`fallback_track_uri` 正規化。
  - ※ 裸 `yes` / `10.5` / `""` は YamlDotNet が string 束縛 → `TryParse` が false → `E-CFG-MISSING-FIELD-001`
    （YAML 1.1 の bool 強制は string ターゲットでは起きない＝Mac の最終 throw 分岐と同じ終状態）。
- `ProgramLengthStore`: 書込→読戻し往復、不在 null、不正値 null（一時ディレクトリへ隔離）。

---

## 9. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。
- エラーコード**追加なし**（config = `E-CFG-MISSING-FIELD-001`、runtime = `E-RTM-SEGMENT-FAILED-001`）。
- **実機確認（要 VOICEVOX + Spotify Premium）**:
  - 長さ 10 で放送し、トーク 2 本ごとにお便り → ニュースが入り、最後に ED で終わる。
  - 放送中に「放送を停止」で即静寂（完全静寂 §3-1）。
  - 途中で「ED で終了」を押すと §3-4 の動作（現セグメント + 準備済み直後トークを流して ED）で優雅に終わる。
  - メニュー「番組の長さ」（10/20/30）の変更が次回放送に反映され、アプリ再起動後も保持される。
  - **NativeMenu のサブメニューネスト**（「番組の長さ ▸」）が Windows トレイで描画されるか。
  - **NativeMenu のチェックマーク**が Windows トレイで描画されるか（描画不可なら §6-2 フォールバックへ）。

---

## 10. 確定設計判断（W13）

1. **`ProgramLength` = `readonly record struct`**（連想値 enum 不在のため。`RawValue`/`TryParse` で Mac の文字列表現に忠実、§3-5 で新クラス増やさない）。
2. **`ProgramBlueprint` は s13 §2 の素のフィールドのみ**（weeklyCast/guest/artistFeature/journal は後続スライスで追加。W13 で前借りしない）。
3. **`PreparationLedger` を per-index linked CTS 化**（「ED で終了」の「1 本だけ残す」を実現。Mac の per-`Task.cancel()` 相当）。
4. **番組長の永続化は Infra の小ヘルパ `ProgramLengthStore`**（`%LOCALAPPDATA%`・平文。新 interface 不要、解析は Core `ProgramLength.TryParse`）。
5. **エンドレスは UI 非露出（Core のみ Mac 忠実に保持）**（ユーザー決定 2026-06-16。トーク 10/20/30 で十分長い。`ProgramLength.Endless`/`ProgramPlan` エンドレス分岐は移植・テスト維持するがメニューに出さない。将来 UI 露出は数行）。
6. **進捗カウンタ `(5/23)` は defer**（エンドレス非露出で `∞` ケースが消え可視確認の主目的が無くなったため。`BroadcastComposition.RunAsync` は `control` のみ追加。後続で onEvent 二股により容易に追加可。Core の `BroadcastEvent` に UI 専用ケースは足さない）。

Mac リファレンス: `Program.swift`（型）/ `BroadcastEngine.swift`（進行・ED・ledger）/ `MenuBar.swift`（UI）/
`ProgramConfig.swift`（v2 ローダ）/ `BroadcastWiring.swift`（長さ解決・plan 構築）。
