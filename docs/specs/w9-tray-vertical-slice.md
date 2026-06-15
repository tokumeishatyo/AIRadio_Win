# W9 — トレイ常駐 + 開始/停止 + 1 コーナーが鳴る最小 E2E（縦切り）

> 対応 Mac 版: `Sources/AIRadioApp/MenuBar.swift`（メニューバー常駐 UI）+ `Sources/AIRadioCore/BroadcastSession.swift`（放送ライフサイクル）。
> design.md §6 が「基盤(W0-W4)の次に必ず通す最重要スライス」と位置づける縦切り。**製品の核（トレイ常駐で 1 コーナー鳴る）を最初に通す**（旧版最大の反省）。

## 1. 概要
Avalonia の **タスクトレイ常駐**アプリを通す。トレイアイコン + メニュー（開始/停止/終了）から、放送を 1 つのキャンセル可能 Task として開始/停止する。
W9 の「1 コーナー」は **OP テーマ演出 1 本**（W4 `ThemeSequencer`）= BGM を検索 → tagline → ダッキング発話 → アウトロ → 停止。
番組進行（複数コーナー・ローリング準備）は W7/W10、番組長 UI・ED で終了は W13、単一インスタンス（Mutex）は W-Win。

## 2. スコープ
**in**:
- `BroadcastSession`（Core）: 放送ライフサイクル状態機械（`Idle`/`Broadcasting`）。`Start(Func<CancellationToken,Task>)` / `Stop()` / `StopAndWaitAsync()` / `CurrentState` / `OnStateChange` コールバック。放送は単一 Task + 内蔵 CTS、停止は `Cancel()`、完了で自動 `Idle` 復帰。多重開始は false。（Mac `actor BroadcastSession` → `lock` ガードに写像、§design）
- Avalonia トレイアプリ（App）:
  - `App.axaml`/`App.axaml.cs`: `Application`、`ShutdownMode.OnExplicitShutdown`、**MainWindow なし**。`TrayIcon` + `NativeMenu` を生成。
  - `TrayController`: トレイアイコン + メニュー（状態ラベル / 「放送を開始」⇄「放送を停止」動的ラベル / 終了）。`BroadcastSession` を保持し、状態変化を `Dispatcher.UIThread` 経由でメニューへ反映。
  - `MinimalBroadcast`（Composition）: 放送本体（OP テーマ 1 本）。**config を毎回新規ロード**（YAML 即反映）→ BGM 検索 → `ThemeSequencer.RunAsync`。
  - 完全静寂（§3-1）: 停止/終了は CTS を Cancel → `ThemeSequencer` の後始末 `PauseIgnoringCancellation` で BGM 停止。終了（Quit）は graceful（`StopAndWaitAsync` で後始末完了を待ってから `Shutdown`）。
- エントリ再構成: `Program.Main`（`[STAThread]` 必須）— 引数なし=トレイ起動、引数あり=`DemoRunner`（W1-W4 のデモを退避）。`BuildAvaloniaApp`。
- トレイアイコン資産（`Assets/tray.ico`）+ Avalonia パッケージ（`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` 12.0.3）。
- テスト（Core）: `BroadcastSession`（開始→Broadcasting→完了でIdle、多重開始拒否、Stop でキャンセル伝播、StopAndWait 完了待ち、再開可能、OnStateChange 発火順）。

**out（後続）**: 番組進行・複数コーナー（W7）／ローリング先読み（W10）／番組長 UI・ED で終了・チェックマーク付きサブメニュー（W13）／単一インスタンス Mutex（W-Win）／起動時設定不正のダイアログ表示（W9 はコンソールへログ）。

## 3. UI / 操作
- トレイアイコン常駐（専用ウィンドウなし）。メニュー項目:
  - 状態（無効・表示専用）: 「停止中」/「放送中…」。（Mac の「停止中（直近エラー: コード）」サフィックスは `BroadcastEvent.segmentFailed` を持つ番組進行が前提のため **W7 以降**。W9 はエラーをコンソールへログ。）
  - 「放送を開始」⇄「放送を停止」（放送状態で**動的にラベル切替**）
  - （区切り）
  - 「終了」: graceful shutdown（放送停止 → 後始末 pause 完了待ち → 終了）
- 状態変化は `BroadcastSession.OnStateChange` → `Dispatcher.UIThread.Post` でメニューラベルへ反映。
- **design §9 の検証点**: Avalonia `NativeMenuItem.Header` の動的更新・`IsEnabled` 制御が満たせるかを実機で確認する（満たせなければ設計会話へ戻す）。

## 4. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（`BroadcastSession` は単体テスト。トレイ UI と放送音声は手動）。
- 実機（要 `-- spotify-auth` 済み + VOICEVOX 起動 + Premium + アクティブデバイス）:
  1. 引数なしで起動 → タスクトレイにアイコンが出る。
  2. メニュー「放送を開始」→ OP テーマが鳴る（tagline → BGM フル → ダッキング発話 → アウトロ → 停止）。ラベルが「放送を停止」に変わる。
  3. 鳴っている最中に「放送を停止」→ 直ちに無音（完全静寂）。ラベルが「放送を開始」に戻る。
  4. 「終了」→ アプリ終了（鳴っていれば pause 送出後に終了）。

## 5. 実装メモ（C# 固有 / Mac との差）
- `actor BroadcastSession` → `lock` ガードのクラス。Mac の暗黙キャンセルは**内蔵 CTS + 明示 CancellationToken** を放送 Task へ渡して再現（§design）。
- `OnStateChange` は `lock` 内で呼ぶ（Mac の actor 直列化に一致）。UI 実装は **非ブロッキング**（`Dispatcher.UIThread.Post`）であること。
- エントリは top-level 文では `[STAThread]` を付けられないため、明示 `Program.Main` へ再構成。Debug=`Exe`（コンソールにログ）/ Release=`WinExe`（コンソールなし）は維持。
- 起動時設定不正（spotify.local.yaml なし等）は W9 ではコンソールにログ（ダイアログ表示は後続）。config は放送開始ごとに新規ロード。
- `NSStatusItem`/`NSMenu` → Avalonia `TrayIcon`/`NativeMenu`。`NSApplication.accessory`（Dock 非表示）→ `ShutdownMode.OnExplicitShutdown` + MainWindow 未設定。
