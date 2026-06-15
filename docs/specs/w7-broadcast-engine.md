# W7 — 番組進行エンジン（BroadcastEngine + 番組フォーマット設定）

> 対応 Mac 版: `Sources/AIRadioCore/BroadcastEngine.swift`（s7 当時の版）/ `Program.swift`（`ProgramSegment`/`SegmentKind`）/ `Sources/AIRadioInfra/ProgramConfig.swift` / `config/program.yaml`（v1 = `segments:` 明示列）/ `config/themes.yaml`。
> **本スライスは Mac `docs/specs/s7-broadcast-engine.md` と同一の線引き**。Mac の現行コードは s13 以降で進化（v2 部品宣言・ローリング先読み・ED で終了）しているが、W7 は **s7 当時のスコープ**（固定 segment 列を順次実行する fail-tolerant/critical エンジン）に限定する。

## 1. 概要
これまで単発だった各セグメント（W4 テーマ演出 / W6 会話コーナー / W5 ニュース天気）を、
**設定駆動の番組フォーマットに従って 1 本の放送として順次実行する** `BroadcastEngine` を Core に実装する。

- 番組構成は `config/program.yaml` の **`segments:` 明示列**で宣言（順序の入替え・追加が設定のみで可能）。
- 本スライスは現有部品で **OP → トークコーナー → ニュース天気 → ED** を通す。標準番組構成への拡張は部品が揃い次第 `SegmentKind` 追加で近づける。
- 放送全体を 1 つのキャンセル可能 Task で回す（`BroadcastSession`(W9) がホスト、停止は CTS `Cancel()`）。
- **コーナースキップで放送継続**: セグメント失敗は記録して次へ（fail-tolerant）。`critical`（既定 OP）だけは中止。
  キャンセルは即時伝播し、どの経路でも最後は必ず `Pause`（完全静寂, §3-1）。

## 2. スコープ

**in**:
- Core `SegmentKind`（enum）: `Opening` / `Talk` / `News` / `Ending`（W7 の 4 種。`Song`=W10・`ArtistFeature`=W15 は後続で追加）。
- Core `ProgramSegment`（record）: `(SegmentKind Kind, string? CornerId = null, bool Critical = false)`。`CornerId` は `Talk` のみ必須。news の読み手は anchor（dj_id は v2/W13）。
- Core `ProgramFormat`（record）: `(string Title, string AnchorDjId, IReadOnlyList<ProgramSegment> Segments)` = program.yaml v1 のパース結果。
- Core `BroadcastThemes`（record, W7 最小=anchor 単一・フラット）: `(ThemeConfig Opening, string OpeningAnnouncement, ThemeConfig News, ThemeConfig Ending, string EndingAnnouncement)`。OP/ED の announcement は themes.yaml の固定文言、news の announcement は実行時のニュース原稿で差し替えるため News は staging（+tagline）のみ。
- Core `BroadcastEvent`（abstract record 階層, `CornerEvent` と同形）: `SegmentStarted(Index,Kind)` / `SegmentFinished(Index,Kind)` / `SegmentFailed(Index,Kind,Code,Detail)` / `BroadcastFinished`。値等価（テストは全ペイロード比較）。`SongStarted/Finished`=W10・`EndingRequested`=W13 は後続。
- Core **`BroadcastEngine`**（具象クラス）— セグメント列を宣言順に逐次実行。`§4` の進行規則。
  - `opening` / `ending`: `ThemeSequencer.RunAsync(theme, announcement, anchorSpeakerId, ct)`（W4）。
  - `news`: **ニュースデリゲートで原稿取得** → `ThemeSequencer.RunAsync(News テーマ, 原稿, anchorSpeakerId, ct)`。
  - `talk`: `CornerEngine.RunAsync(corner, djs, ct)`（W6, 準備+本番を同期実行）。`corner_id` で `corners` 参照。
- Core `BroadcastException`（Errors.cs 追加）: `SegmentFailed(detail)` → `E-RTM-SEGMENT-FAILED-001`（critical 中止の throw 媒体）。
- Infra `ProgramConfig`（`program.yaml` v1 → `ProgramFormat`。`YamlConfigLoader<T>` + DTO + map/validate）。必須欠落は `ConfigException.MissingField`（fail-fast）。
- Infra `ThemesConfig`（`themes.yaml` → `BroadcastThemes`。W4 が W7 へ委譲したローダ）。OP/news/ED の staging + tagline + 固定 announcement。W7 はフラット（`by_dj` は W13.5）。
- App: composition root が config を毎回新規ロードして engine を組み、`BroadcastSession.Start(ct => engine.RunAsync(...ct))` で実行。**W9 の `MinimalBroadcast` を本エンジンが置換**（トレイの「放送を開始」が OP→talk→news→ED の 1 本を流す）。デモ `broadcast`（headless 通し再生 + Ctrl-C で完全静寂確認）。
- テスト（Core, fake 注入）: 進行順序 / セグメント失敗のスキップ継続 / critical 中止 / キャンセル即時停止 + pause / ニュース原稿の組み込み / preflight fail-fast / イベント列の同一性。Infra: `ProgramConfig`（正常 / 必須欠落 fail-fast）。

**設計判断（Mac との差・interface を作らない）**:
- Mac s7 はテスト容易性のため `CornerRunning` / `AnnouncementProviding` protocol を追加した。**Win は §3-5 によりこれらを作らず**、`ThemeSequencer`(W4) / `CornerEngine`(W6) を **Core の具象クラスのまま**結線する（いずれも実装 1 個）。
- **ニュース seam（確定: デリゲート）**: `NewsWeatherProvider`(W5) は Infrastructure にあり Core から参照不可（§3-4 単方向）。エンジンは `Func<CancellationToken, Task<string>>` を注入で受け、App が `ct => newsWeatherProvider.AnnouncementAsync(ct)` を渡す。新 interface ゼロで Core 純粋、テストは固定文字列を返す関数で差し替え。W11 で 2 実装目（`LlmNewsScriptProvider`）が出た時点で interface 昇格を再検討。
- `BroadcastEngine` は単一実装のため **interface 化しない**（具象）。コンストラクタ肥大（旧版 18 引数）回避: **ctor は協調オブジェクトのみ**（`ThemeSequencer` / `CornerEngine` / news デリゲート / `ISpotifyController` / `onEvent`）、**番組データ（`ProgramFormat` / `BroadcastThemes` / `corners` / `djs`）は `RunAsync` 引数**。`tts`/`audio`/`llm`/`http` はエンジンに直接渡さず、`ThemeSequencer`/`CornerEngine` 経由で間接到達（Mac と同方針）。

**out（後続）**:
- ローリング先読み準備（`PreparationLedger`, window=2, 先行 prepare）→ **W10**。W7 は talk/news を**同期 prepare→run**で実行（無音は許容、W10 で排除）。
- 冒頭曲 song セグメント（`SegmentKind.Song` / `SongSegmentSpec` / `{first_song}` OP 曲振り）→ **W10**。
- コーナー数 N 駆動生成（`ProgramPlan` / `ProgramLength` / `talk,talk,letter,news` パターン / endless）・「ED で終了」（`BroadcastControl` / `EndingRequested`）・番組長 UI・program.yaml **v2 部品宣言**への移行 → **W13**。
- お便り（Letter コーナー実行）→ W12 ／ 曜日替わり DJ・`{greeting}`/時刻展開・コーナー頭二層化・themes.yaml `by_dj` → W13.5 ／ 時刻アナウンス → W8 ／ ゲスト → W14 ／ アーティスト特集 → W15 ／ LLM ニュース会話化 → W11 ／ ステーション・ジャーナル → W18。

## 3. 番組フォーマット設定

### 3-1. `config/program.yaml`（v1 = 明示 segment 列）
```yaml
program:
  title: "ケイラボAIラジオ"
  anchor_dj_id: zundamon      # OP/news/ED を読む DJ（djs.yaml の id）
  segments:                   # 上から順に実行
    - type: opening
      critical: true          # 既定 false。失敗時スキップせず放送中止（OP のみ true）
    - type: talk
      corner_id: free_talk    # corners.yaml の id を参照（talk は必須）
    - type: news
    - type: ending
```
- `anchor_dj_id` は `djs.yaml` に存在しなければ fail-fast。`talk` の `corner_id` 欠落・未定義 id は fail-fast。
- `opening`/`news`/`ending` の演出・文言は `themes.yaml`（§3-2）。本スライスで内容は変更しない。
- v2 部品宣言（`default_length`/`song`/`letter`/`news.dj_id` 等）は W13 で導入。本ローダ（v1）は W13 で置換される前提。

### 3-2. `config/themes.yaml`（W7 ローダ範囲, フラット）
- `opening` / `news` / `ending` 各に BGM 演出（`track_uri`/`intro_seconds`/`volume`/`ducked_volume`/`outro_seconds`）。
- `opening`: `tagline` + `announcement`（anchor の固定口上）。`news`: `tagline`（announcement は実行時のニュース原稿で差し替え）。`ending`: `announcement`（`tagline` なし = いきなり BGM）。
- **W7 の announcement は静的**（`{greeting}`/`{first_song}`/時刻系プレースホルダは含めない）。時刻展開は W8、`{greeting}`/`by_dj` は W13.5、`{first_song}` は W10 で導入。

## 4. BroadcastEngine の進行規則（Mac s7 §4 準拠）

1. **preflight（fail-fast, 音を出す前）**: `anchor_dj_id` を `djs` で解決（不在→`ConfigException`）。各 `talk` セグメントの `corner_id` を `corners` で解決（不在→`ConfigException`）。失敗時は `ThemeSequencer`/`CornerEngine`/Spotify を一切呼ばない。
2. セグメントを宣言順に実行。セグメント間に追加の無音処理は挟まない（各セグメントが自身の演出で開始・終了）。各セグメントは開始時 `SegmentStarted(index,kind)`、成功時 `SegmentFinished(index,kind)` を通知。
3. **fail-tolerant**: セグメントがキャンセル以外で失敗したら、`PauseIgnoringCancellation` で当該セグメントを静音化し、`SegmentFailed(index, kind, code, detail)` を通知して**次へ進む**。`code = (ex as IRadioError)?.Code ?? ex.GetType().Name`、`detail = ex.Message`。
   - ただし `Critical` セグメントは失敗時に放送中止: `BroadcastException.SegmentFailed("{kind}: {code}")`（`E-RTM-SEGMENT-FAILED-001`）を throw。
4. **キャンセル**: `OperationCanceledException`（または `ct.IsCancellationRequested` が true）は即時再 throw。後続セグメントは実行しない。**`SegmentFailed` を出さない**。Infra がキャンセルをドメインエラーにラップして投げても（取消された HttpClient 等）、`ct.IsCancellationRequested` を見てスキップと誤判定しない。
5. **完全静寂（§3-1）**: 正常終了・失敗・キャンセルのいずれでも最後に必ず `Pause`。中止・キャンセル経路は `PauseIgnoringCancellationAsync`（キャンセル済み `HttpClient` でも pause を送り切る）で送ってから再 throw。正常終了は `PauseAsync` の上で `BroadcastFinished` を通知。
6. イベント: `SegmentStarted` / `SegmentFinished` / `SegmentFailed` / `BroadcastFinished`。**`BroadcastFinished` は正常完走時のみ**（中止・キャンセルでは出さない）。

## 5. エラーコード
`E-RTM-SEGMENT-FAILED-001`（RTM, W7）は台帳 `error-codes.md` に登録済み。本スライスで `BroadcastException.SegmentFailed` として Errors.cs に実装する（critical 中止 / fail-tolerant スキップ双方の表現）。設定不正は既存 `E-CFG-MISSING-FIELD-001`（fail-fast）。新規コード追加なし。

## 6. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（fake 注入、ネットワーク・音声非依存）。
- テストで「中間セグメントの失敗 → スキップして最後まで進行 + pause」「critical(OP) 失敗 → 中止 + pause」「キャンセル → 即停止 + pause（`SegmentFailed` 不発）」が検証されている。
- 実機（要 `spotify-auth` 済み + VOICEVOX + Premium + アクティブデバイス + `llm.local.yaml` の api_key）:
  1. `dotnet run --project AIRadio.App -- broadcast` で **OP（テーマ演出）→ フリートーク（会話 + 一曲）→ ニュース天気（実データ + テーマ演出）→ ED** が 1 本通しで流れる（ユーザー確認）。
  2. 放送中に Ctrl-C で速やかに停止し**完全に静寂**になる（ユーザー確認）。
  3. トレイ「放送を開始」でも同じ 1 本が流れ、「放送を停止」で即静寂（W9 の縦切りが MinimalBroadcast から本エンジンへ差し替わる, ユーザー確認）。

## 7. 実装メモ（C# 固有 / Mac との差）
- **ctor / RunAsync 分割**: `BroadcastEngine(ThemeSequencer themeSequencer, CornerEngine cornerRunner, Func<CancellationToken,Task<string>> newsAnnouncement, ISpotifyController spotify, Action<BroadcastEvent>? onEvent = null)` / `RunAsync(ProgramFormat format, BroadcastThemes themes, IReadOnlyList<CornerTemplate> corners, IReadOnlyList<DjProfile> djs, CancellationToken ct)`。
- **同期 prepare→run（W7/W10 境界）**: talk は `cornerRunner.RunAsync(corner, djs, ct)`（準備+本番を 1 呼び出し）。先読みなし。テストで「prepare→run が同一セグメント内で隣接、未来セグメントを先行準備しない」をガードする。W10 で `PrepareAsync`/`RunAsync(prepared)` の窓 2 に置換。
- **ニュースデリゲート**: news セグメントは `var script = await newsAnnouncement(ct);` → `themeSequencer.RunAsync(themes.News, script, anchorSpeakerId, ct)`。`NewsWeatherProvider` は取得失敗を握り潰しキャンセルのみ throw（W5）ので、エンジン側にニュース固有のエラー処理は不要（キャンセルは §4-4 で再 throw）。
- **anchor speakerId**: OP/news/ED は全て anchor が読む。`anchorSpeakerId = djs.First(d => d.Id == format.AnchorDjId).SpeakerId`（preflight で存在保証済み）。news の dj_id（別読み手）は v2/W13。
- **完全静寂の二重化**: 各セグメント（`ThemeSequencer`/`CornerEngine`）が自前で pause する上に、エンジンの最後でも重ねて保証（Mac 同様）。中止・キャンセル経路は `ISpotifyController.PauseIgnoringCancellationAsync`（W3 拡張）。
- **announcement 展開**: W7 は静的文言のためプレースホルダ展開なし。`TemplateExpander`（未知キーは原文保持）は時刻系導入スライス（W8）で配線する。
- **BroadcastEvent / CornerEvent**: `abstract record` 階層（`CornerEvent` と同形）。値等価でテストは全ペイロード比較。`onEvent` は同期 `Action<BroadcastEvent>?`（UI 反映は呼び出し側が `Dispatcher.UIThread.Post`, W9 と同方針）。
- **新規 Core 型**: `Program.cs`（`SegmentKind` / `ProgramSegment` / `ProgramFormat` / `BroadcastThemes`）、`BroadcastEngine.cs`（`BroadcastEvent` + `BroadcastEngine`）、`Errors.cs` に `BroadcastException`。**新規 Infra 型**: `Config/ProgramConfig.cs`、`Config/ThemesConfig.cs`。
