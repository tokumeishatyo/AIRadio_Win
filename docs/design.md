# ケイラボAIラジオ Windows版 設計（design.md）

> 本書は Windows 版（C#/.NET + Avalonia, タスクトレイ常駐）の**設計 SoT**。
> 挙動の正は Mac 版実装（`../AIRadio_Mac-main/`）。本書はその**移植設計**を定義する。
> Confluence『Win版 設計』は本書のミラー。横断ルールは `CLAUDE.md`、機能別の挙動契約は `docs/specs/*.md` を参照。

---

## 1. システム概要

ケイラボAIラジオは、**タスクトレイ常駐型の自律 AI ラジオ**。作業用 BGM として、AI DJ（VOICEVOX 音声）が気ままに喋り、Spotify の楽曲を流し続ける。核は **論理的進行管理（番組フォーマット） × カオス的コンテンツ生成（LLM）** の両立。

### エンドツーエンドの放送ループ（Mac 実装準拠）

1. **起動**: アプリはトレイに常駐（メイン画面なし）。VOICEVOX は利用者が手動起動（BYO）。Spotify は Premium アカウントでデスクトップアプリにログイン済み（Connect の "Computer" デバイスとして利用）。
2. **開始（トレイ「放送を開始」）**: composition root が `config/*.yaml` を**毎回新規ロード**（YAML 編集が再起動なしで反映）。設定/秘密情報の欠落は **fail-fast** でダイアログ表示し中断。放送開始時に VOICEVOX `/user_dict` へ発音辞書を冪等同期（fail-tolerant）。
3. **番組生成**: `ProgramPlan` が「トークコーナー本数 N」から決定論的にセグメント列を生成。レイアウトは `opening(0) → song(1) → 本体[talk, talk, letter, news]の繰り返し → ending`。ゲストコーナーは最初のニュース直後に 1 回、アーティスト特集はゲスト直後に 1 回挿入。
4. **放送実行（1 つのキャンセル可能 Task）**: `BroadcastEngine` が `preparationWindow=2` の**ローリング先読み準備**で各セグメントを 2 つ先まで裏で準備（LLM 台本生成 + 全行 TTS 事前合成 + 選曲プレフライト）。これにより放送中の無音をゼロにする。各セグメントを順次実行:
   - **opening / news / ending**: 統一 `ThemeSequencer` が `tagline → BGM フル音量 → intro 待機 → ducked へ降下 → 発話 → outro へシーク → フル音量に戻し余韻 → 停止` を実行。
   - **song**: プレフライト済み `TrackInfo` を再生し、`playSeconds` 経過または曲終了検出（`WaitForTrackToFinish`）まで待機。
   - **talk**: 2 DJ の会話台本を話者ごとの VOICEVOX 音声で順次再生し、確定済みの 1 曲を流す。
   - **artistFeature**: Spotify トップトラックから最大 7 曲を 3+3+1 で構成し、intro → グループ紹介 → 連続再生 → コメント → 固定アウトロ。
5. **横断不変条件**: §5 を参照（完全静寂・プレフライト・fail-fast/tolerant）。
6. **終了**: 「ED で終了」で現コーナー完了後に ED へ。「終了（Quit）」は **graceful shutdown**（放送停止 → Spotify pause 完了待ち → 終了）。正常終了時のみステーション・ジャーナル（週次長期記憶）を保存。

---

## 2. アーキテクチャ

3 層 + テスト。**依存方向 App → Infrastructure → Core**（Core は外部依存ゼロ、何も参照しない）。

- **Core**: ドメイン型（`record`）、抽象（`interface`）、放送進行（`BroadcastEngine` / `BroadcastSession` / `ProgramPlan` / 各 Generator / `ThemeSequencer` 等）。fake のみで全テスト可能。
- **Infrastructure**: 外部実装（YamlDotNet / NAudio / HttpClient / DPAPI / Gemini / VOICEVOX / Spotify / Research）。
- **App**: Avalonia トレイ常駐 + composition root（DI 配線）。

**抽象境界（interface を置くのはここだけ。§3-5 CLAUDE.md）**:
`ILLMBackend` / `ITTSBackend` / `IAudioPlayer` / `ISpotifyController`（+`ITrackSearcher`） / `IResearchSource` / `IClock` / `IJournalStore` / `IHttpClient` / `ITokenStore`。
これ以外は具象クラスで実装し、interface を量産しない。

### Swift → C# の並行モデル写像（要再現）
- 放送全体 = 単一 `Task` + `CancellationTokenSource`。`stop()`→`Cancel()`、`stopAndWait()`→`Cancel(); await task`。
- `actor`（`BroadcastSession` / `SpotifyAuth`）→ `SemaphoreSlim` / `lock` でガード。
- `Task.checkCancellation()` → `ct.ThrowIfCancellationRequested()`。`CancellationError` → `OperationCanceledException`。
- `PreparationLedger`（`[Int: Task]`）→ `lock` ガードの `Dictionary<int, Task<T>>` + 各 prep に linked CTS、`CancelAll()`。
- `pauseIgnoringCancellation` → `await Task.Run(() => Pause(), CancellationToken.None)`（キャンセル済みでも pause を送り切る）。
- `async let` プリフェッチ → `var prefetch = tts.SynthesizeAsync(next); await audio.PlayAsync(current); current = await prefetch;`（無音排除）。

---

## 3. Mac → Windows 移植マップ

**【要判断】** はユーザー決定済み／後続確認の行（§8・§9 参照）。

| Mac コンポーネント / プロトコル | C#/.NET 同等物（採用） | 備考・リスク |
|---|---|---|
| Core ドメイン型（`struct`/`enum`, `Sendable`, `Equatable`） | `record` / `record struct`（値等価・`with`）。payload 付き enum は record 階層 | 直接移植。Core 外部依存ゼロ維持 |
| Core プロトコル（`I` なし） | `IXxx` インタフェース。protocol 拡張のデフォルト実装 → **拡張メソッド** | 直接移植 |
| 放送オーケストレーション（`BroadcastEngine` / `actor BroadcastSession`） | `class` + 単一 `Task` + `CancellationTokenSource`。actor → `SemaphoreSlim`/`lock` | 設計の心臓部 |
| 協調並行（structured concurrency） | `Task`/`await` + 明示 `CancellationToken` を全 I/O に伝播 | Swift は暗黙、C# は明示 |
| `PreparationLedger` | `lock` ガード `Dictionary<int, Task<T>>` + linked CTS | キャンセルは明示 CTS |
| `pauseIgnoringCancellation` | `Task.Run(Pause, CancellationToken.None)` を await | 意図的に再現必須 |
| `async let` プリフェッチ | prefetch して再生中に裏で合成 | 無音排除パターン |
| YAML（Yams + Codable） | **YamlDotNet** + DTO `record`。**汎用 `YamlConfigLoader<T>` 1個** | 旧版 21 ローダの反省 |
| HTTP（`URLSession` / `HTTPClient` protocol） | `HttpClient`（`IHttpClient` 抽象）+ `HttpClientStatusException(int statusCode)` | 404/403 を呼び出し側でパターンマッチ |
| WAV 再生 + 完了待ち + キャンセル即停止（`AVAudioPlayer`） | **NAudio**（`WaveFileReader`→`WaveOutEvent`、`PlaybackStopped` で完了待ち、`Stop()` で即停止） | **【要検証】最高リスク**。Mac は duration+0.2s スリープ近似、NAudio はイベント駆動 → 聴覚検証（W1） |
| Spotify Web API 再生（`WebApiSpotifyController`） | 直接移植（純 Web API）。`SpotifyAPI-NET` 流用も可 | Premium + アクティブデバイス必須 |
| OAuth PKCE（`SpotifyAuth` + `PKCE`） | `RandomNumberGenerator` + `SHA256.HashData`（base64url）。actor → `SemaphoreSlim` | RFC 7636 ベクタでテスト |
| ループバック受信（`LoopbackServer`） | `System.Net.HttpListener`（`http://127.0.0.1:{port}/`） | URL-ACL / FW / 一回限りライフサイクル注意 |
| ブラウザ起動（`NSWorkspace.open`） | `Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })` | 直接移植 |
| AppleScript Spotify フォールバック | **移植しない（ドロップ）** | Windows に Spotify ローカル自動化 API なし |
| 秘密トークン保管（`KeychainTokenStore`） | **DPAPI**（`ProtectedData`, `CurrentUser`, `%LOCALAPPDATA%`）。旧版 `DpapiSecureTokenStore` 流用可 | 高リスク（秘密情報） |
| VOICEVOX TTS（`VoicevoxTTS`） | 直接移植（`127.0.0.1:50021`, `audio_query`→`synthesis`）。旧版 `VoicevoxTtsBackend` 流用可 | VOICEVOX は Windows ビルドあり |
| VOICEVOX ユーザー辞書（`VoicevoxUserDict`） | 直接移植。NFKC→NFC = `Normalize(FormKC)`→`FormC`。カタカナ判定 = `Rune` レンジ | v0.25.2 quirk 再現 |
| Gemini LLM（`GeminiLLMBackend`） | 直接移植（`System.Text.Json` + `HttpClient`, `x-goog-api-key`）。旧版 `GeminiLlmBackend` 流用可 | |
| ニュース RSS（`NewsRssSource` SAX） | `System.Xml.XmlReader`（ストリーミング） | 低リスク |
| JMA 天気 / リサーチ | 直接移植（JSON/文字列ロジック） | |
| ジャーナル永続化（`YamlJournalStore`） | YamlDotNet + `System.IO.File`。`journal.local.yaml` 流用 | ISO 週キー = `ISOWeek.GetWeekOfYear` |
| `SystemClock` | `IClock`（`Now: DateTimeOffset`, `DelayAsync`）。`Task.Delay` | 直接移植 |
| トレイ UI（`NSStatusItem` + `NSMenu`, `.accessory`） | **Avalonia `TrayIcon` + `NativeMenu`**、`ShutdownMode.OnExplicitShutdown`、`MainWindow` 未設定 | **【要検証】** NativeMenu のチェックマーク/動的ラベル制約（W9）|
| コマンド有効化（`validateMenuItem`） | `CanExecute`（CommunityToolkit.Mvvm `RelayCommand`） | 放送↔生成の排他、ED は放送中のみ |
| 番組長の永続化（`UserDefaults`） | 設定ファイル（`%LOCALAPPDATA%` 配下 JSON/YAML） | |
| 単一インスタンス | 名前付き `Mutex` | 新規（2 トレイの競合防止）|
| 自動起動 | `HKCU\...\Run` or スタートアップフォルダ（トグル） | Mac 未実装。Windows 固有スライスで |
| エラー通知（`NSAlert`） | Avalonia メッセージボックス / トレイバルーン | |
| **AquesTalk（Windows 専用追加）** | **AquesTalk1（初代）**。`ITTSBackend` 実装としてスロットイン。Core 変更不要 | **【最後に実装】** §9 参照（旧版の AquesTalk10 P/Invoke は流用不可）|

---

## 4. プロジェクト構成

旧 Windows 版（281 .cs / 40 interface / 23 namespace / 1117 行の God オブジェクト）の**過剰分解を踏襲しない**。

```
AIRadio_Win-main/
├── AIRadio.sln
├── AIRadio.Core/                 (依存: なし) ドメイン型(record) + 抽象(I*) + 放送進行
│   ├── Models/                   DJProfile, TrackInfo, PlayerState, ProgramSegment, ThemeConfig, JournalEntry ...
│   ├── Abstractions/             I* + 拡張メソッド(WaitForTrackToFinish, PauseIgnoringCancellation)
│   ├── Broadcasting/             BroadcastEngine, BroadcastSession, PreparationLedger, ProgramPlan
│   ├── Corners/                  CornerEngine, ArtistFeatureEngine, ListenerLetterGenerator
│   ├── Scripting/                DialogueScriptGenerator, NewsScriptGenerator, SongPicker,
│   │                             ThemeSequencer, TemplateExpander, TimePhrases, SeasonPhrases, DailyCalendar
│   ├── Memory/                   StationJournal
│   └── Errors/                   RadioError + 例外（安定コード）
├── AIRadio.Infrastructure/       (依存: YamlDotNet, NAudio, HttpClient, ProtectedData, System.Text.Json)
│   ├── Http/  Llm/  Tts/  Audio/  Spotify/  Research/  Persistence/  Security/  Config/
│   │          (Config = 汎用 YamlConfigLoader<T> + DTO record + map/validate)
│   └── (後で) Tts/AquesTalk1Backend + AquesTalk1Native (P/Invoke)
├── AIRadio.App/                  (Avalonia エントリ。トレイのみ)
│   ├── App.axaml(.cs)            ShutdownMode.OnExplicitShutdown, TrayIcon
│   ├── TrayMenu/  Composition/  Settings/  SingleInstance/
├── AIRadio.TestSupport/          fake 一式
├── AIRadio.Core.Tests/  AIRadio.Infrastructure.Tests/   (xUnit)
├── config/                       *.yaml（Mac から流用）, *.local.yaml.sample
└── native/                       AquesTalk1 DLL（最後）
```

**旧版との決定的な違い**: interface は実差し替え境界だけ／Config+Loader は汎用 1 個／進行ロジックは引数肥大を避けステップ列で表現。

---

## 5. 横断不変条件

- **完全静寂**: 停止/キャンセル/エラーのあらゆる経路が最終的に Spotify `Pause()` で終わる。後始末 pause は `CancellationToken.None`。
- **プレフライト**: 曲名を含む DJ 台詞を生成する**前**に再生可否を確認。失敗時は無名フォールバック曲。
- **fail-fast vs fail-tolerant**: 起動時設定不正と critical セグメント（OP）失敗のみ中断。放送事故ゼロ系（プレフライト/BGM/ニュース取得/個別 LLM・TTS 失敗、ジャーナル、読み辞書）はログして継続。詳細は `docs/specs/error-codes.md`。

---

## 6. ビルド順序（スライス）

Mac の s0–s19 進行を踏襲。**製品の核（トレイ常駐で 1 コーナー鳴る = W9）を最初の縦切りで通す**ことを最優先（旧版最大の反省）。

| 段階 | スライス | 内容 |
|---|---|---|
| 基盤 | **W0** | .NET ソリューション骨格 + Core 抽象 + ドメイン型 + テスト土台（製品コード外部依存ゼロ）|
| | W1 | VOICEVOX TTS + NAudio WAV 再生（完了待ち + キャンセル即停止）+ 汎用 `YamlConfigLoader<T>` |
| | W2 | Spotify PKCE 認証 + Web API 検索 + `IsPlayable` プレフライト + LoopbackServer(HttpListener) + DPAPI トークン保管 |
| | W3 | Spotify Web API 再生（play/pause/volume/seek/state + 終端検知 `WaitForTrackToFinish` + 完全静寂 `PauseIgnoringCancellation`）|
| | W4 | 統一 ThemeSequencer（OP/news/ED の BGM ダッキング演出）|
| **縦切り** | **W9** | **トレイ常駐 + メニュー + 開始/停止 + 1 コーナーが鳴る最小 E2E**（最重要）|
| 部品 | W5 | ニュース・天気（News RSS + JMA、fail-tolerant）|
| | W6 | Gemini 台本生成 + 2-DJ トークコーナー + SongPicker |
| | W7 | BroadcastEngine（番組進行 + **冒頭曲 + ローリング先読み準備**、`program.yaml`、fail-tolerant/critical、news 専任読み手）|
| | W8 | 時刻・日付アナウンス（音量/話速は global `playback_volume`/`speed_scale` として W1 実装・配線済み・Mac 一致のため W8 対象外, 2026-06-15 確定）|
| シームレス | ~~W10~~ | ~~切れ目ない放送（冒頭曲セグメント + ローリング先読み準備）~~ → **W7 に統合済み**（無音ゼロのため前倒し）。エンドレス連続放送は W13 の `ProgramPlan` へ |
| | W11 | ニュースの LLM 会話化 |
| 番組構造 | W12 | 季節コンテキスト + テーマプール + お便りコーナー |
| | W13 | コーナー数駆動の番組生成 + 「ED で終了」 + 番組長 UI |
| | W13.5 | 曜日替わりメイン DJ + コーナー頭二層化 |
| 特殊 | W14 | ゲストコーナー（最初のニュース直後に 1 回）|
| | W15 | アーティスト特集（3+3+1）+ アーティスト一覧生成 |
| 仕上げ | W16 | DJ の今日の気分 + テーマ宣言リード文 |
| | W17 | DailyContext 拡張（記念日の軽重）|
| | W18 | ステーション・ジャーナル（週次長期記憶、正常終了時のみ保存）|
| | W19a | 発音辞書（VOICEVOX ユーザー辞書冪等同期）|
| | W19b | アーティスト読み自動登録 |
| Windows 固有 | **W-Win** | 単一インスタンス（Mutex）、自動起動トグル、graceful shutdown 配線 |
| **最後** | **W-AQT** | **AquesTalk1 TTS バックエンド**（`ITTSBackend` にスロットイン、Core 変更なし）|

---

## 7. 旧 Windows 版からの流用 / 破棄

### 流用（移植価値高・粒度も健全）
- `.csproj` / TFM 構成（`Nullable enable`, `AvaloniaUseCompiledBindingsByDefault`, `InternalsVisibleTo`, `config/` の PreserveNewest コピー）。
- パッケージ: YamlDotNet, NAudio, System.Security.Cryptography.ProtectedData, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, SpotifyAPI.Web(.Auth)。
- 実装: `VoicevoxTtsBackend.cs`, `GeminiLlmBackend.cs`, `SpotifyClient.cs` + `DpapiSecureTokenStore.cs`, `NAudioMixer.cs`, `HttpEndpointProbe.cs` / `ProcessLauncher.cs`（VOICEVOX 自動起動の布石）。
- パターン: decorator（`NormalizingTtsBackend` 等）、セキュア注入（`*.local.yaml` gitignore + DPAPI + ログマスク + pre-commit secret-scan）。
- `config/*.yaml`（表示文言・DJ 設定・暦データ）。**ただしロード機構は汎用ローダで作り直す**。
- ※ AquesTalk は **AquesTalk1 を使う**ため旧版（AquesTalk10 + AqKanji2Koe）の P/Invoke はそのまま流用不可（§9）。

### 破棄（salvage しない）
- `ScheduledProgramProducer.cs`（1117 行/18 引数の God）、`ProgramProducer.cs`、`App.axaml.cs` の DI 配線（525 行）、`MainWindowViewModel.cs`（819 行）、21 個の `*ConfigLoader.cs`、実装 1 個限りの多数の `I*.cs`。
- 反省: 「1 spec=1 namespace」、Config+Loader の二重化、進行ロジックの引数肥大、thrash spec（同一問題の再仕様化）、生成品質に進捗を握らせる構造。

---

## 8. 確定設計判断（2026-06-15）

- **Spotify** = Web API 一本化（Premium + ログイン済みアクティブデバイス前提）。AppleScript 等価フォールバックはドロップ。
- **LLM** = Gemini のみ（`gemini-3.1-flash-lite`, Mac 完全一致）。`ILLMBackend` 抽象は維持し将来差替可能に。
- **配布** = まずは開発実行のみ（`config/` を実行ファイル隣、Mac 同一レイアウト、YAML 即反映）。zip/インストーラは後決定。
- **AquesTalk** = `djs.yaml` の `tts_backend: voicevox|aquestalk` で DJ ごと選択。**AquesTalk1（初代）を使用**。実装は最後。
- 開発環境: **Visual Studio 2026 + .NET 10 SDK**（旧 Windows 版踏襲）。技術: .NET 10 + Avalonia 12 / 音声=NAudio / YAML=YamlDotNet（汎用 Loader 1 個）/ トークン=DPAPI / 単一インスタンス=名前付き Mutex。
- **ログ / デバッグ起動**: Mac 版（パッケージ前はコンソールで会話内容まで全ログを流す）を踏襲。`IRadioLog` 抽象 + `ConsoleRadioLog` 実装。**Debug=`OutputType=Exe`（コンソールサブシステム）で標準出力に全ログをストリーム**、Release=`WinExe`（コンソールなしのトレイ常駐）。秘密値はマスク。将来 Release 向けファイル出力（`%LOCALAPPDATA%`）を seam で追加可能。
- **プロンプト/番組名**: Mac 版は Core にハードコード。まず同等にハードコード移植で完全一致を取り、`prompts.yaml` 等の外部化は後段。番組名「ケイラボAIラジオ」は最初から定数 1 箇所に集約。
- 進め方 = Mac と同じ **spec 駆動**（`docs/specs` 先行 → Confluence ミラー → 実装 → `dotnet test` 緑 → 更新）。ただし「1 spec=1 namespace」禁止（CLAUDE.md §3-5）。

---

## 9. 未確定・後続で詰める点

- **AquesTalk1**: DLL 名・関数シグネチャ（`AquesTalk_Synthe` 系、音声記号列＝かな入力、WAV 返却）・利用する声種（声ごとに別 DLL）・**漢字→かな変換手段**（AqKanji2Koe ライセンスの有無／別手段）。**最終スライス（W-AQT）着手時にユーザーへ確認**。
- **NAudio 完了待ち**: Mac の `duration+0.2s` スリープ近似と `PlaybackStopped` イベント駆動の挙動差 → W1 で聴覚検証。
- **Avalonia NativeMenu**: チェックマーク（番組長サブメニュー）・動的ラベル（開始⇄停止）・有効化制御が満たせるか → W9 で早期検証。満たせなければ設計会話に戻す。
- **配布形態と autostart**: zip / インストーラ、起動方式 → Windows 固有スライスで決定。
- **VOICEVOX 自動起動**: Mac は BYO 手動。旧版 `HttpEndpointProbe`/`ProcessLauncher` を使った自動起動管理を Windows 差別化機能として入れるか → 後段オプション。
