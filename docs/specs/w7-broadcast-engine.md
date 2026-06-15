# W7 — 番組進行エンジン（BroadcastEngine + 冒頭曲 + ローリング先読み準備）

> 対応 Mac 版: `Sources/AIRadioCore/BroadcastEngine.swift` / `Program.swift`（`ProgramSegment`/`SegmentKind`/`SongSegmentSpec`）/ `Sources/AIRadioInfra/ProgramConfig.swift` / `config/program.yaml` / `config/themes.yaml`。
> **W7 は Mac の番組進行エンジンを移植する**。Mac `s7-broadcast-engine`（番組進行 + fail-tolerant/critical）に加え、**実運用で無音区間を出さないため `s10`（ローリング先読み準備）と `s13` の冒頭曲セグメントを W7 に取り込む**（当初 W10 予定 → **W10 を W7 へ統合**, design.md §6）。コーナー数 N 駆動生成・「ED で終了」・番組長 UI（v2）は W13。

## 1. 概要
これまで単発だった各セグメント（W4 テーマ演出 / W6 会話コーナー / W5 ニュース天気）を、`config/program.yaml` の **明示 `segments:` 列**に従って 1 本の放送として順次実行する `BroadcastEngine`（Core 具象）を実装する。本スライスのレイアウトは **OP → 冒頭曲 → トークコーナー → ニュース天気 → ED**。

**無音ゼロの肝（ローリング先読み準備）**: 実行中セグメントの先 2 つ（window=2）の準備（選曲 / LLM 台本 + 全行 TTS / ニュース原稿）を裏で起動・保持する。OP と冒頭曲（フル再生・数分）の再生中に次のトーク台本が準備され、**セグメント間の無音をゼロにする**（放送中の完全無音, §3-1）。冒頭曲の選曲は OP 前に確定し、OP 末尾で `{first_song}`（曲振り）として読む。

放送全体を 1 つのキャンセル可能 Task（`BroadcastSession` がホスト）で回し、停止は CTS `Cancel()`。コーナー失敗はスキップして継続（fail-tolerant）、`critical`（既定 OP）だけは中止。キャンセルは即時伝播し、どの経路でも最後は必ず `Pause`（完全静寂）。

## 2. スコープ

**in**:
- Core `SegmentKind`（`Opening` / `Song` / `Talk` / `News` / `Ending`。`ArtistFeature` は W15）。
- Core `SongSegmentSpec`（冒頭曲: `FallbackTrackUri` / `PromptHint` / `Volume` / `PlaySeconds`）。
- Core `ProgramSegment`（`Kind` / `CornerId?`（talk）/ `Critical` / `Song?`（song）/ `DjId?`（news 専任読み手））+ `ProgramFormat`（`Title` / `AnchorDjId` / `Segments`）。
- Core `BroadcastThemes`（OP/news/ED の `ThemeConfig` + OP/ED の固定 announcement。W7 は anchor 単一・フラット）。
- Core `BroadcastEvent`（`SegmentStarted` / `SegmentFinished` / `SegmentFailed` / `SongStarted` / `SongFinished` / `BroadcastFinished`、値等価）。
- Core **`BroadcastEngine`**（具象）— セグメント列を宣言順に逐次実行 + **ローリング先読み準備（window=2, 内部 `PreparationLedger`）**。
  - `opening` / `ending`: `ThemeSequencer.RunAsync`（W4）。OP は `{first_song}` を展開。
  - `song`（冒頭曲）: 先行選曲済みの `TrackInfo` を再生（`PlaySeconds>0` で固定秒 / 0 でフル再生 = `WaitForTrackToFinish`）→ pause。`SongStarted`/`SongFinished` 通知。
  - `talk`: 先行準備済み `PreparedCorner` を `CornerEngine.RunAsync`（W6）。
  - `news`: **専任読み手（`dj_id`、未指定は anchor）**でニュースデリゲートの原稿を news テーマ演出で読む。
- Core `BroadcastException`（`E-RTM-SEGMENT-FAILED-001`、critical 中止の throw 媒体）。
- Infra `ProgramConfig`（`program.yaml` v1 → `ProgramFormat`。song/news.dj_id 解析、`track_uri` 正規化）/ `ThemesConfig`（`themes.yaml` → `BroadcastThemes`）。
- App `BroadcastComposition`（config 毎回新規ロード → フル DI グラフ → エンジン 1 回実行。`SongPicker` 配線。W9 `MinimalBroadcast` を置換）/ デモ `broadcast`（Ctrl-C で完全静寂）/ トレイが本エンジンを実行。
- テスト（Core, fake 注入 / Infra ローダ）。ローリング先読み（トーク準備が冒頭曲の再生より前に始まる）を含む。

**設計判断（Mac との差・interface を作らない）**:
- Mac の `CornerRunning` / `AnnouncementProviding` / `SongPicking` / `ThemeSequencing` protocol は **Win では作らず**、`ThemeSequencer`(W4) / `CornerEngine`(W6) / `SongPicker`(W6) を Core の **具象クラスのまま**結線する（§3-5、いずれも実装 1 個）。
- **ニュース seam = デリゲート（確定）**: `NewsWeatherProvider`(W5) は Infrastructure にあり Core から参照不可（§3-4）。エンジンは `Func<CancellationToken, Task<string>>` を注入で受け、App が `ct => provider.AnnouncementAsync(ct)` を渡す。news 出現のたびに呼ぶ。W11 で 2 実装目が出た時点で interface 昇格を再検討。
- **ローリング準備の写像**: Mac の `PreparationLedger`（非構造化 Task + `cancelAll`）→ Win は `lock` ガード `Dictionary<int, Task<T>>` + **エンジン token に linked CTS**（停止で連動キャンセル）+ `DrainAsync`（未消費分を観測）。ctor は協調オブジェクトのみ（ThemeSequencer / CornerEngine / SongPicker / news デリゲート / ISpotifyController / IClock / onEvent）、番組データは `RunAsync` 引数（旧版 18 引数の轍を踏まない）。

**out（後続）**:
- コーナー数 N 駆動生成（`ProgramPlan` / `ProgramLength` / `talk,talk,letter,news` パターン / **エンドレス連続放送**）・「ED で終了」（`BroadcastControl` / `EndingRequested`）・番組長 UI・program.yaml **v2 部品宣言** → **W13**。
- お便り → W12 ／ 曜日替わり DJ・`{greeting}`/時刻展開・コーナー頭二層化・themes.yaml `by_dj` → W13.5 ／ 時刻アナウンス → W8 ／ ゲスト → W14 ／ アーティスト特集 → W15 ／ LLM ニュース会話化 → W11 ／ ステーション・ジャーナル → W18 ／ 英語曲タイトル等の読み正確化（読み辞書） → W19a/b。

## 3. 番組フォーマット設定

### 3-1. `config/program.yaml`（v1 = 明示 segment 列）
```yaml
program:
  title: "ケイラボAIラジオ"
  anchor_dj_id: zundamon       # OP/ED を読む DJ（djs.yaml の id）
  segments:                    # 上から順に実行
    - type: opening
      critical: true           # 既定 false。失敗時スキップせず放送中止（OP のみ true）
    - type: song               # 冒頭曲（OP 直後の「本日の1曲目」。再生中に次のトークを準備）
      song_prompt_hint: "一日の始まりや番組の幕開けに合う、前向きで広く知られた曲"
      fallback_track_uri: "spotify:track:5jsqaNOAbeBG5QYL7JpySJ"
      volume: 100
      play_seconds: 0          # 0 = フル再生（この間にトーク台本を準備）
    - type: talk
      corner_id: free_talk     # corners.yaml の id（talk は必須）
    - type: news
      dj_id: ryusei            # ニュース・天気の専任読み手（未指定は anchor）
    - type: ending
```
- `anchor_dj_id` 不在・`talk` の `corner_id` 欠落・`song` の `fallback_track_uri` 欠落・`news.dj_id` の未定義・未知 `type` は fail-fast（`E-CFG-MISSING-FIELD-001`）。
- v2 部品宣言は W13 で導入し本ローダ（v1）を置換する。

### 3-2. `config/themes.yaml`（W7 フラット・anchor）
- `opening`/`news`/`ending` 各に BGM 演出（`track_uri`/`intro_seconds`/`volume`/`ducked_volume`/`outro_seconds`）。`opening` は tagline + announcement（**末尾に `{first_song}`**）、`news` は staging（+tagline、announcement は実行時注入）、`ending` は announcement（tagline なし）。
- `{first_song}` は冒頭曲の曲振り（`<artist>で、「<title>」`、曲名不明時は「本日の一曲」）。`{greeting}`/時刻系は W8/W13.5、`by_dj` は W13.5。

## 4. BroadcastEngine の進行規則

1. **preflight（fail-fast、音を出す前）**: `anchor_dj_id` を `djs` で、各 `talk` の `corner_id` を `corners` で、`news.dj_id`（指定時）を `djs` で解決。失敗時は演出・選曲・Spotify を一切呼ばない。
2. **ローリング先読み準備（window=2）**: ループ前に先 2 つの準備を起動し、冒頭曲(index 1)の選曲を待って `{first_song}` を作る（放送開始前の無音は許容）。各セグメント実行開始時に `index+2` までの準備を起動、消費後に破棄。準備内容: talk = `CornerEngine.PrepareAsync`（台本 + 全行 TTS）/ song = `SongPicker.PickAsync`（全滅・失敗は fallback 曲）/ news = ニュースデリゲート / opening・ending = なし。
3. セグメントを宣言順に逐次実行。各セグメントで `SegmentStarted` → 成功で `SegmentFinished`。消費時は準備済み Task を `await`（通常は完了済みで待ち時間ゼロ）。
4. **fail-tolerant**: 失敗セグメントを静音化（`PauseIgnoringCancellation`）し `SegmentFailed(index, kind, code, detail)` を通知して次へ。`critical` は `BroadcastException`（`E-RTM-SEGMENT-FAILED-001`）で放送中止。
5. **キャンセル**: `OperationCanceledException` / `ct.IsCancellationRequested` は即時 throw、`SegmentFailed` と誤判定しない。保持中の準備 Task は linked CTS で連動キャンセルし `DrainAsync` で観測。
6. **完全静寂（§3-1）**: 正常終了・失敗・キャンセルのいずれでも最後に必ず `Pause`。中止・キャンセル経路は `PauseIgnoringCancellationAsync`。正常完走のみ `BroadcastFinished`。

## 5. エラーコード
`E-RTM-SEGMENT-FAILED-001`（RTM, W7）は台帳に登録済み。`BroadcastException.SegmentFailed` として実装。設定不正は既存 `E-CFG-MISSING-FIELD-001`（fail-fast）。新規コード追加なし。

## 6. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（fake 注入、ネットワーク・音声非依存）。
- テスト: 進行順序（OP→冒頭曲→トーク→ニュース→ED）/ **ローリング先読み（トーク準備が冒頭曲再生より前）** / `{first_song}` 展開 / 冒頭曲フル再生の終端検知 / news の専任読み手 / fail-tolerant スキップ / critical 中止 / キャンセル即時 + pause / preflight fail-fast / イベント列の同一性 / ローダ（song・dj_id 解析、必須欠落 fail-fast）。
- 実機（要 VOICEVOX + Premium + アクティブデバイス + `llm.local.yaml` の api_key）:
  1. `dotnet run --project AIRadio.App -- broadcast`（またはトレイ）で **OP → 冒頭曲（OP 末尾で曲振り）→ トーク → ニュース天気（龍星が読む）→ ED** が**無音区間なく**1 本通しで流れる（ユーザー確認済み）。
  2. 放送中 Ctrl-C で速やかに停止し**完全に静寂**になる（ユーザー確認済み）。

## 7. 実装メモ（C# 固有 / Mac との差）
- **ローリング準備**: `EnsurePrepared(through)` がローカル進捗（`preparationStartedThrough`）を前進させ、kind 別に準備 Task を起動して `PreparationLedger` に保持。消費は `await ledger.XxxTask(index)`。停止は `CancellationTokenSource.CreateLinkedTokenSource(ct)` を準備 Task に渡し、`finally` で `Cancel()` + `DrainAsync()`（未観測例外を残さない）。
- **冒頭曲**: `SongPicker.PickAsync(SongRequest(context, fallbackUri, hint))`。選曲失敗（LLM 不調・503 等）はフォールバック曲に倒し放送を止めない。`{first_song}` は選曲確定後に `<artist>で、「<title>」`（曲名空は「本日の一曲」）。**英語タイトルは VOICEVOX が不明瞭 → 読み辞書 W19**。
- **ニュースデリゲート**: news セグメントは `await newsAnnouncement(ct)` → `themeSequencer.RunAsync(themes.News, 原稿, 専任 speakerId, ct)`。`NewsWeatherProvider` は取得失敗を握り潰しキャンセルのみ throw（W5）。
- **完全静寂の二重化**: 各セグメント（ThemeSequencer/CornerEngine/song の pause）が自前で止める上に、エンジンの最後でも重ねて保証。
- **announcement 展開**: `TemplateExpander`（未知キー原文保持）で `{first_song}` を展開。時刻系（`{greeting}` 等）の配線は W8/W13.5。
- **新規型**: Core `Program.cs`（SegmentKind/SongSegmentSpec/ProgramSegment/ProgramFormat/BroadcastThemes）・`BroadcastEngine.cs`（BroadcastEvent + BroadcastEngine + PreparationLedger）・`Errors.cs` の `BroadcastException`。Infra `Config/ProgramConfig.cs`・`Config/ThemesConfig.cs`。
