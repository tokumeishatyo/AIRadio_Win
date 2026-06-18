# W-Win — Windows ライフサイクル（単一インスタンス / VOICEVOX 自動起動 / graceful shutdown）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。**Windows 固有スライス＝Mac リファレンスなし**（Mac はメニューバー常駐で単一性は OS 任せ・VOICEVOX は BYO 手動）。
> design.md §2 シーム表（単一インスタンス=Mutex / 自動起動）・スライス表 W-Win 行・§198（VOICEVOX 自動起動の後段オプション）を実体化。
> ユーザー判断（2026-06-18）: アプリのログイン時自動起動は**不要**。代わりに**本アプリ起動時に VOICEVOX を自動起動**したい。セッション終了時 graceful はベストエフォートで入れる。

## 1. 概要・スコープ

タスクトレイ常駐アプリ（W9）の Windows ライフサイクルを整える 3 本柱:

1. **単一インスタンス**: 名前付き `Mutex` で 2 重起動を防ぐ（2 トレイが同時に Spotify を制御する事故を防止）。
2. **VOICEVOX 自動起動**: アプリ起動時にエンドポイントが未応答なら VOICEVOX を起動する（exe パスは tts.yaml 設定優先・未設定は標準インストール先を探索）。完全 fail-tolerant（見つからない/失敗でも放送系は従来どおり＝BYO 手動にフォールバック）。
3. **graceful shutdown**: 明示「終了」は W9 で実装済み（放送停止 → Spotify pause 完了待ち → 終了）。本スライスで**セッション終了（ログオフ/シャットダウン）時のベストエフォート pause** を追加。

スコープ外: アプリのログイン時自動起動（ユーザー不要判断でドロップ）。VOICEVOX の起動完了待ち・バージョン検証（未応答時は既存の fail-tolerant `E-TTS-UNREACHABLE-001` に委ねる）。VOICEVOX 終了時の連動停止。

## 2. 単一インスタンス（`SingleInstance` + Mutex）

```csharp
// AIRadio.Infrastructure/Platform/SingleInstance.cs
public sealed class SingleInstance : IDisposable
{
    public bool IsFirstInstance { get; }
    public SingleInstance(string name);   // new Mutex(false, name, out createdNew)。IsFirstInstance = createdNew
    public void Dispose();                // Mutex を解放（プロセス終了で OS が破棄）
}
```

- 名前付き Mutex の**存在**で多重起動を検出する（所有はしない＝`initiallyOwned: false`。`createdNew` が false なら既に他インスタンスが存在）。Mutex オブジェクトをプロセス寿命の間保持し、終了時にハンドルが閉じて名前が破棄される。
- 名前は `Local\` スコープ（ユーザーセッション内一意で十分。例 `AIRadioWinTraySingleInstance`）。
- 配線: `Program.Main` の**トレイモード（引数なし）でのみ**取得。`IsFirstInstance == false` なら**サイレントに終了**（`return 0`・ログのみ）。**CLI デモ（引数あり）は Mutex を取らない**（複数同時実行・放送と並行を許容）。`using var single = ...` で保持し、`StartWithClassicDesktopLifetime` の間生かす。
- テスト: 同一プロセス内で同名 `SingleInstance` を 2 つ生成 → 1 個目 `IsFirstInstance==true`・2 個目 `false`（1 個目を生かしたまま）。

## 3. VOICEVOX 自動起動（`VoicevoxLauncher`）

```csharp
// AIRadio.Infrastructure/Tts/VoicevoxLauncher.cs
public enum VoicevoxLaunchResult { AlreadyRunning, Launched, NotConfigured, LaunchFailed }

public sealed class VoicevoxLauncher
{
    // endpoint・http は VoicevoxTTS と共有。exePath は解決済み（null=未発見）。launch は注入（既定 Process.Start）。
    public VoicevoxLauncher(string endpoint, IHttpClient http, string? exePath, Action<string>? launch = null);
    public Task<VoicevoxLaunchResult> EnsureRunningAsync(CancellationToken ct = default);

    // 解決: 設定優先（存在チェックせず＝誤設定は LaunchFailed で検知）・未設定は候補を File.Exists 探索（W-Win §3）。
    public static string? ResolveExePath(string? configuredPath);
    public static string? ResolveExePath(string? configuredPath, IReadOnlyList<string> candidates, Func<string, bool> exists);  // テスト可（InternalsVisibleTo 未設定のため public）
}
```

- **probe**: `GET {endpoint}version`。応答あり（2xx）または `HttpStatusException`（サーバが応答）→ 起動済み（`AlreadyRunning`）。`HttpRequestException`（接続不可）→ 未起動。その他例外 → 安全側で起動済み扱い（spurious launch を避ける）。
- 未起動かつ `exePath == null` → `NotConfigured`（BYO 手動。ログのみ）。
- 未起動かつ `exePath != null` → `launch(exePath)`（既定 `Process.Start(new ProcessStartInfo(exePath){ UseShellExecute = true })`）→ `Launched`。例外は全捕捉 → `LaunchFailed`。**throw しない**（fail-tolerant）。
- **パス解決**: `configuredPath`（tts.yaml `voicevox.exe_path`）が非空なら**それを使う（存在チェックなし）**。未設定なら候補を `File.Exists` で探索:
  - `%LOCALAPPDATA%\Programs\VOICEVOX\VOICEVOX.exe`
  - `%PROGRAMFILES%\VOICEVOX\VOICEVOX.exe`
  - `%LOCALAPPDATA%\Programs\voicevox\VOICEVOX.exe`
  最初に存在したものを返す。無ければ null。
- **新 interface は作らない**: probe は既存 `IHttpClient` シーム、起動は `Action<string>` デリゲート（W7 ニュース seam と同じく実 1 個の interface を避ける＝§3-5）。パス解決はファイル存在を `Func<string,bool>` で注入してテスト可能（candidates も注入オーバーロード）。
- 配線: アプリ起動時（`TrayController` ctor）に**バックグラウンド fire-and-forget** で `EnsureRunningAsync` を実行し結果をログ。放送開始時には起動済み。完全 fail-tolerant（throw しない）。

## 4. graceful shutdown（セッション終了）

- 明示「終了」（W9 `ShutdownAsync`）は不変: 生成キャンセル → `StopAndWaitAsync` → トレイ非表示 → `desktop.Shutdown(0)`。
- **セッション終了**: Avalonia `IClassicDesktopStyleApplicationLifetime.ShutdownRequested` を購読（Windows ではログオフ/シャットダウンの `WM_QUERYENDSESSION` を Avalonia が拾って発火。`Microsoft.Win32.SystemEvents` パッケージ不要）。
  - ハンドラ: 明示終了処理中（`_isShuttingDown`）でなければ best-effort で `StopAndWaitAsync` を**短いタイムアウト付き同期待ち**（OS は数秒で kill するため bounded）。`e.Cancel` は立てない（終了を妨げない）。例外は全捕捉。
  - 自前 `desktop.Shutdown(0)` は `ShutdownRequested` を**発火しない**（`TryShutdown` のみ発火）ため、W9 の Quit 経路と二重処理にならない。`_isShuttingDown` で念のためガード。
- **完全静寂（§3-1）**: セッション終了でも放送中なら Spotify を best-effort で pause（ログオフでローカル Spotify も閉じるため実害は小だが、原則に沿う）。

## 5. config 変更（tts.yaml）

`voicevox.exe_path`（任意）を追加。未設定なら標準インストール先を探索（§3）。

```yaml
voicevox:
  endpoint: "http://127.0.0.1:50021/"
  credit: "VOICEVOX:{speaker}"
  # exe_path: "C:\\Users\\<you>\\AppData\\Local\\Programs\\VOICEVOX\\VOICEVOX.exe"  # 任意。未設定なら標準インストール先を探索
```

`TtsConfig` record に `string? VoicevoxExePath` を追加（DTO `VoicevoxDto.ExePath`＝`voicevox.exe_path`）。既存 yaml は欠落許容＝null（後方互換）。

## 6. 配線

- `Program.Main`: 引数なし（トレイ）のとき先頭で `using var single = new SingleInstance("AIRadioWinTraySingleInstance");` → `!IsFirstInstance` なら `Console`/log に一言出して `return 0`。`single` は `StartWithClassicDesktopLifetime` の間保持。引数ありは従来どおり `DemoRunner`。
- `TrayController` ctor: tts.yaml をロードして `VoicevoxLauncher` を組み立て、`_ = EnsureVoicevoxAsync()`（バックグラウンド・結果ログ）。`desktop.ShutdownRequested += OnShutdownRequested`。tts.yaml ロード失敗は VOICEVOX 自動起動をスキップ（ログのみ・放送系は各々が再ロードするため無影響）。
- `App.OnFrameworkInitializationCompleted` は不変（TrayController が全部持つ）。

## 7. エラー / fail-tolerant

- **新エラーコードを増やさない**。VOICEVOX 起動失敗・未発見・2 重起動・セッション終了 pause 失敗はいずれもログのみ（放送を止めない）。VOICEVOX 未応答は合成時の既存 `E-TTS-UNREACHABLE-001` に集約。
- 単一インスタンスの 2 個目はサイレント終了（エラーではない＝設計どおり）。

## 8. テスト（`dotnet test AIRadio.slnx` グリーンが完了条件）

- `SingleInstance`: 同名 2 個 → 1 個目 first・2 個目 not first（1 個目を生かしたまま）。異なる名前 → 両方 first。Dispose 後に同名再取得 → first に戻る。
- `VoicevoxLauncher.EnsureRunningAsync`（fake `IHttpClient` + 記録用 `Action<string>`）:
  - probe 応答あり（responder が bytes 返す）→ `AlreadyRunning`・launch 呼ばれない。
  - probe 接続不可（`HttpRequestException`）＋ exePath あり → `Launched`・launch にそのパスが渡る。
  - probe 接続不可 ＋ exePath null → `NotConfigured`・launch 呼ばれない。
  - probe 接続不可 ＋ launch が throw → `LaunchFailed`（throw しない）。
  - probe が `HttpStatusException` → `AlreadyRunning`（サーバ応答）。
  - GET URL が `…/version` を指す。
- `VoicevoxLauncher.ResolveExePath`（注入 candidates + `Func<string,bool>`）:
  - 設定パスあり → 存在チェックせずそのまま返す。
  - 未設定 → 最初に存在する候補を返す／全滅なら null。
- `TtsConfig`: `voicevox.exe_path` ありで `VoicevoxExePath` セット・無しで null（後方互換）。既存テスト（exe_path 無し yaml）が緑のまま。
- graceful shutdown のセッション終了配線は UI（Avalonia ライフタイム）依存のため**実機/手動確認**（§9）。`BroadcastSession.Stop/StopAndWaitAsync` のロジック自体は既存テストで担保。

## 9. 受け入れ基準（実機・最後に一括）

1. **単一インスタンス**: トレイ常駐中にもう一度 exe を起動 → 2 個目は即終了し、トレイアイコンは 1 個のまま。
2. **VOICEVOX 自動起動（設定）**: tts.yaml に `exe_path` を設定し VOICEVOX を落とした状態でアプリ起動 → VOICEVOX が自動で立ち上がる。起動済みなら二重起動しない。
3. **VOICEVOX 自動起動（探索）**: `exe_path` 未設定でも標準インストール先にあれば自動起動。無ければ何もしない（従来どおり手動起動でも放送は回る）。
4. **graceful shutdown（明示）**: トレイ「終了」→ 放送停止・Spotify pause・トレイ消滅（W9 既存）。
5. **graceful shutdown（セッション終了）**: 放送中に Windows からログオフ/シャットダウン → best-effort で Spotify が pause される（ログオフで Spotify も閉じるため確認は限定的）。

## 10. Win 固有 確定設計判断

- A. 単一インスタンス = 名前付き Mutex（存在検出・非所有）。2 個目はサイレント終了。CLI デモは対象外。
- B. 自動起動 = **アプリ自身のログイン時自動起動は実装しない**（ユーザー判断）。代わりに**VOICEVOX をアプリ起動時に自動起動**（design §198 の後段オプションを前倒し）。
- C. VOICEVOX exe パス = tts.yaml 設定優先・未設定は標準インストール先探索。設定パスは存在チェックせず（誤設定は LaunchFailed で検知）。
- D. 新 interface を作らない: probe=既存 `IHttpClient`、起動=`Action<string>` デリゲート（§3-5）。
- E. セッション終了 = Avalonia `ShutdownRequested`（`Microsoft.Win32.SystemEvents` 不要）。best-effort・bounded・throw しない。
- F. 新エラーコードなし（すべてログのみ・fail-tolerant）。
