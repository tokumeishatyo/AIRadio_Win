# ケイラボAIラジオ Windows版 プロジェクトルール（CLAUDE.md）

> 本ファイルは「コードを書くたびに必要となる横断ルール」だけを置く。
> 要件 (WHAT) / 設計 (HOW) / 機能別仕様の詳細は `docs/` と Confluence に集約し、必要時のみ参照する（§5）。
> CLAUDE.md は毎セッション必ずコンテキストを消費するため、肥大化を避ける。

本プロジェクトは Mac 版『ケイラボAIラジオ』(Swift) の**挙動を Windows (C#/.NET + Avalonia) で忠実に再現**する移植版。

- Mac 版リポジトリ `../AIRadio_Mac-main/`（**挙動リファレンス＝正**。読み取り専用、編集禁止）。
- 失敗した旧 Windows 版 `../AIRadio-main/`（**下回り実装の流用元**。読み取り専用、編集禁止。過剰分解した設計は踏襲しない）。

---

## 1. プロジェクト概要

PC 作業中のバックグラウンド利用を想定した、**タスクトレイ常駐型の自律 AI ラジオ**「ケイラボAIラジオ」。
AI DJ が気ままに喋り、Spotify の曲をかける。論理的進行管理（番組フォーマット）と LLM のカオス的生成を両立。専用ウィンドウは持たない。

挙動の Source of Truth は **Mac 版実装** (`../AIRadio_Mac-main/`)。要件は『Win版 要件定義』、設計は `docs/design.md` と『Win版 設計』を参照（§5）。

---

## 2. 技術スタック（確定）

| カテゴリ | 採用 |
|---|---|
| 言語 / 並行 | C# 13 / .NET 10。async-await + `Task` + **明示的 `CancellationToken`**（Swift の構造的並行の暗黙伝播を明示スレッディングで再現）|
| ビルド | dotnet SDK / MSBuild。3 プロジェクト（Core / Infrastructure / App）+ TestSupport + テスト 2 |
| UI | Avalonia 12（**タスクトレイ常駐**）。`TrayIcon` + `NativeMenu`。専用ウィンドウなし（`ShutdownMode.OnExplicitShutdown`）|
| 設定 | YAML = **YamlDotNet** + `record`。**汎用 `YamlConfigLoader<T>` 1個に集約**（ファイルごとのローダ量産は禁止）|
| HTTP | `System.Net.Http.HttpClient`（Gemini / Spotify Web API / News RSS / 気象庁）|
| 音声再生 | **NAudio**（`WaveFileReader` → `WaveOutEvent`、`PlaybackStopped` で完了待ち、`CancellationToken` で即停止）|
| Spotify 再生制御 | **Web API（OAuth PKCE）一本化**。検索・再生とも。ローカル Spotify を Connect の "Computer" デバイスとして制御（**Premium + ログイン済みアクティブデバイス前提**）。AppleScript 等価のローカル自動化は Windows に存在しないため持たない |
| OAuth コールバック | `System.Net.HttpListener`（`127.0.0.1` ループバック）|
| トークン保管 | **DPAPI**（`ProtectedData`, `CurrentUser`）。`%LOCALAPPDATA%` に暗号化保管。秘密値はログでマスク |
| LLM | **Gemini**（`gemini-3.1-flash-lite`, Google API 無料枠）。`ILLMBackend` 抽象で差し替え可（将来 Ollama 等）|
| TTS | **VOICEVOX**（ローカル HTTP, BYO 手動起動）。`ITTSBackend` 抽象。**Windows 専用追加で AquesTalk1（初代）**を最後に実装。DJ ごとに `djs.yaml` の `tts_backend` で選択 |
| テスト | **xUnit**。`dotnet test` グリーンを各スライスの完了条件とする |
| ログ | 進行ログは標準出力（`Console`）。秘密値は出力しない（§3-3）。`ILogger` 化は将来 |

---

## 3. 横断ルール（全モジュール必須）

### 3-1. 完全静寂（Idle / Stop）の絶対保証
ユーザーが停止した場合、Spotify 制御・LLM リクエスト・TTS 合成・外部リサーチ（News / 天気）を**完全休止**する。
放送全体を 1 つの `Task` で回し、停止は `CancellationTokenSource.Cancel()`。各 await 点で `OperationCanceledException` を伝播させ、
`finally` で Spotify を必ず `Pause`（鳴らしっぱなしにしない）。後始末の `Pause` は**キャンセル非継承**で送る（`CancellationToken.None`。キャンセル済み `HttpClient` はリクエストを送れないため）。

### 3-2. プレフライトチェック原則
DJ が曲紹介テキストを生成する**前**に、該当楽曲の検索・再生可否（`ITrackSearcher.IsPlayable`）を必ず確認する。
不可なら代替曲を選定。「紹介した曲が流れない」放送事故を論理的に排除する。

### 3-3. 機密情報の取扱
API キー / client_id / refresh トークンはソースコード・コミット対象ファイルに**絶対に書かない**。
`config/*.local.yaml`（`.gitignore` 対象, dev）または DPAPI 暗号化保管（配布）経由で注入。ログ出力時はマスク。

### 3-4. YAML 設定ファイルの命名規約
- ファイル名: `kebab-case.yaml` ／ キー名: `snake_case`
- 機密値を含むファイル: `<name>.local.yaml`（`.gitignore` 対象）
- 配置: `config/` ディレクトリに集中（**Mac 版の YAML をそのまま流用**）

### 3-5. spec は「要件契約」、クラス量産の発注書ではない（旧 Windows 版の失敗の核心）
旧版は「1 spec = 1 namespace = 新クラス群」を機械的に展開し **281 .cs / 59 仕様**に肥大化して迷走した。これを繰り返さない。
- 1 つの spec が新 namespace・新 interface を生むとは限らない。近接する spec（OP / ED / Theme / Boot / Greet 系等）は **1〜2 クラスに統合**する。
- interface は**実差し替えが起きる境界だけ**（TTS / LLM / Audio / Spotify ＋ テスト用 Clock / Http / Research / Store）。実装 1 個限りの抽象を作らない。
- Config + Loader は**汎用 `YamlConfigLoader<T>` 1個 + DTO + map/validate**。ファイルごとのローダを作らない。
- 進行ロジックは `BroadcastEngine` に集約するが、**コンストラクタ引数肥大（旧版 18 引数）を避け**、段を小さなステップ列で表現する。
- **製品の核（トレイ常駐で 1 コーナー鳴る）を早期の縦切り（W9）で必ず通す**。

---

## 4. 命名規約・テスト方針

### 4-1. コード内識別子
英語（型 = PascalCase / メソッド = PascalCase / ローカル変数 = camelCase）。日本語表示文字列（DJ 名・コーナー名等）は
**YAML 設定に一元集約**し、業務ロジックに散在させない。

### 4-2. 抽象 interface
C# 慣習に従い **`I` プレフィックスを付ける**（Mac/Swift の "I なし" とは逆）。役割名で
（例: `ILLMBackend` / `ITTSBackend` / `IAudioPlayer` / `ITrackSearcher` / `ISpotifyController` / `IResearchSource` / `IClock`）。
外部依存は必ず interface 越しにアクセスし、テストで fake 差し替え可能にする。

### 4-3. エラーコード
形式 `E-<CAT3>-<DETAIL>-<NNN>`（例: `E-SPT-NO-DEVICE-001`）。C# では例外（`RadioError`）に安定コード文字列（`Code` プロパティ）を持たせる。
カテゴリ: CFG / RTM / SPT / TTS / LLM / NEWS / WX / RES / ART / JNL / PRON。台帳は `docs/specs/error-codes.md`。
放送事故ゼロ系（プレフライト・BGM 失敗・ニュース取得・個別 LLM/TTS 失敗）は fail-tolerant（ログ + 継続）、起動時設定不正は fail-fast。

### 4-4. テスト方針
**xUnit** を使用。外部依存（TTS / LLM / Spotify / Audio / Clock / Research）は interface 越しに fake 差し替え。
Core 層は完全に単体テスト可能とする。**`dotnet test` グリーンを各スライスの完了条件**とする。
聴覚・視覚確認が必要な要素（音声再生・トレイ UI）は実装完了報告で明示する。

---

## 5. ドキュメント索引（SoT と Confluence ミラー）

- ローカル SoT: `docs/design.md`（設計）, `docs/specs/*.md`（機能別仕様）, `docs/specs/error-codes.md`（エラー台帳）
- Confluence: Site `aaic.atlassian.net` ／ Space **AIRadio**（id `28835843`, key `AIRadio`）。Mac 版と同じ階層（ホーム `28835940` 直下）に Win 版ページを配置。

| ページ | id | 用途 |
|---|---|---|
| Win版 要件定義 | `39780354` | 要件 (WHAT) |
| Win版 設計 | `39616606` | 設計 (HOW) のハブ（`docs/design.md` ミラー）|
| Win版 仕様 | `39911425` | 機能別仕様（`docs/specs/` ミラー）のハブ + 実装進捗 |
| Win版 CLAUDE.md | `39551065` | 本ファイルのミラー |
| └ W0 仕様 | `39944193` | `docs/specs/w0-skeleton.md` ミラー（仕様ハブ配下）|

**参照のみ（編集禁止）**: Mac 版ページ（要件定義 `37519362` / 設計 `37617666` / 仕様 `37519389` / CLAUDE.md `37486597`）、旧 Windows 版ページ（要件定義 `28934153` 等）。

### 編集ポリシー（Mac 版と同一要領）
| 種別 | 編集先 |
|---|---|
| 横断ルール | ローカル `CLAUDE.md` → Confluence『Win版 CLAUDE.md』へミラー |
| 要件 | Confluence『Win版 要件定義』 |
| 設計 | `docs/design.md` を SoT → Confluence『Win版 設計』へミラー |
| 仕様 | `docs/specs/<slice>.md` を SoT → Confluence『Win版 仕様』配下にミラー |
| 実装進捗 | Confluence『Win版 仕様』配下のチェックリスト |

---

## 6. ディレクトリ構成

```
AIRadio_Win-main/                 # Windows版プロジェクトルート（git root）
├── AIRadio.sln
├── AIRadio.Core/                 # 抽象(interface) + ドメイン型 + 放送進行（外部依存ゼロ）
├── AIRadio.Infrastructure/       # 外部実装（VOICEVOX / Gemini / Spotify / NAudio / Research / DPAPI / Config）
├── AIRadio.App/                  # Avalonia トレイ常駐 + DI 配線（composition root）。専用ウィンドウなし
├── AIRadio.TestSupport/          # fake 一式（→ Core）
├── AIRadio.Core.Tests/           # xUnit
├── AIRadio.Infrastructure.Tests/ # xUnit
├── config/                       # YAML 設定（Mac 版から流用。機密は *.local.yaml）
├── native/                       # AquesTalk1 DLL（最終スライスで配置・.gitignore 対象）
├── docs/
│   ├── design.md                 # 設計 SoT
│   └── specs/                    # 機能別仕様 SoT（w0.. + error-codes.md）
└── CLAUDE.md
```
依存方向は外側 → 内側（App → Infrastructure → Core）。Core は何も参照しない。

---

## 7. 仕様駆動ワークフロー（Mac 版と同一）

1. `docs/specs/<slice>.md` を先に書く（仕様凍結）
2. git commit
3. Confluence『Win版 仕様』配下に同内容 + 実装チェックリストをミラー作成
4. 実装 + テスト（`dotnet test` グリーン）
5. git commit
6. Confluence でチェックボックス更新

※ §3-5 を厳守。**spec を増やすこと自体は進捗ではない。**

---

## 8. その他

- `../AIRadio_Mac-main/`（Mac 版）と `../AIRadio-main/`（旧 Windows 版）は**読み取り専用の参考**。編集・コミット・push 禁止。
- 本 CLAUDE.md は Windows 版リポジトリに**コミット可**。ユーザーレベル CLAUDE.md は持ち込まない・コミットしない。
- 確定した方針（Spotify Web API 一本化 / LLM=Gemini / 配布=開発実行 / AquesTalk1 / .NET10+Avalonia12 等）は `docs/design.md` の「確定設計判断」を参照。
