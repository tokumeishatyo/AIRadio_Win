# W0 — .NET ソリューション スケルトン + Core 抽象 + テスト土台

> 対応する Mac 版スライス: `s0-skeleton`。挙動・スコープを C#/.NET に写像する。

## 1. 概要
Windows 版の土台。3 層 + テストの .NET ソリューションを作り、Core の抽象（interface）群・ドメイン型（record）・
エラーパターン・純粋ユーティリティ、テスト用 fake 一式、最小 App エントリを用意して **`dotnet test` グリーン**を確立する。
実エンジン（VOICEVOX / Gemini / Spotify）と YAML ロードは後続スライス（W1 以降）。

## 2. スコープ

**in**:
- `AIRadio.sln` と各プロジェクト:
  - `AIRadio.Core`（classlib, 依存なし）
  - `AIRadio.Infrastructure`（classlib, → Core）
  - `AIRadio.TestSupport`（classlib, → Core。fake 一式）
  - `AIRadio.App`（executable, → Core/Infrastructure。最小スタブ：`ConsoleRadioLog` で起動バナーを出力し終了。**`OutputType` を Debug=`Exe`（コンソール）/ Release=`WinExe`** で切替。Avalonia / トレイ UI は W9 で追加）
  - `AIRadio.Core.Tests` / `AIRadio.Infrastructure.Tests`（xUnit）
- `AIRadio.Core`:
  - interface: `ILLMBackend` / `ITTSBackend` / `IAudioPlayer` / `ITrackSearcher` / `ISpotifyController` / `IResearchSource` / `IClock` / `IRadioLog`（ログ seam）
  - DTO（record）: `LLMRequest` / `TrackInfo` / `PlayerState` / `PlaybackState`
  - エラー: `RadioError`（`Code` / `Message`）+ `SpotifyError` / `ConfigError`（または `RadioError` に安定コード）
  - 純粋 util: `TemplateExpander`（`{key}` 置換、テンプレート展開の土台）
- `AIRadio.Infrastructure`: `SystemClock`（実 `IClock`）/ `ConsoleRadioLog`（実 `IRadioLog`。標準出力へストリーム、秘密値マスク）
- `AIRadio.TestSupport`: `EchoLLM` / `InMemoryTTS` / `FakeClock` / `SpyAudioPlayer` / `FakeTrackSearcher` / `FakeSpotifyController` / `FakeResearchSource`
- テスト: `TemplateExpander` / `RadioError`（コード） / fakes / `SystemClock`
- `docs/specs/error-codes.md` 台帳の起票
- `.gitignore`

**out（後続）**:
- YAML 設定ロード（YamlDotNet + 汎用 `YamlConfigLoader<T>`）→ W1
- 実エンジン（VOICEVOX / NAudio / Gemini / Spotify）→ W1+
- 統一テーマエンジン → W3
- トレイ常駐 UI → W9

## 3. 受け入れ条件
- `dotnet build` 成功（.NET 10 SDK / Visual Studio 2026）
- `dotnet test` 全グリーン
- **製品コードの外部 NuGet 依存ゼロ**（`Core` / `Infrastructure` / `App` とも標準ライブラリのみ。Avalonia は W9 で追加。テストは xUnit）
- 依存方向 **App → Infrastructure → Core** を厳守（**Core は何も参照しない**）
- **Debug ビルドをコンソールから起動すると、起動バナー（ログ）が標準出力に流れる**（Mac 版のコンソール起動踏襲。会話/台本ログは W1 以降で本格化）

## 4. エラーコード（本スライスで起票）
| コード | 発生条件 |
|---|---|
| `E-SPT-NO-DEVICE-001` | 再生可能な Spotify がない |
| `E-SPT-API-FAILED-001` | Spotify 操作の一般失敗 |
| `E-CFG-MISSING-FIELD-001` | 設定の必須フィールド欠落 |

## 5. テスト戦略
- `TemplateExpander`: 既知キー置換 / 未知キーは原文保持。
- `RadioError`: 各エラーの `Code` 文字列が安定キーと一致。
- fakes: `EchoLLM` がプロンプトをエコー / `InMemoryTTS` が非空データ / `FakeClock.DelayAsync` 即時 /
  `FakeSpotifyController` が呼び出し順を記録 / `FakeTrackSearcher` がクエリ記録と結果返却。
- `SystemClock`: `DelayAsync(0)` が完了し、`Now` が単調。

## 6. 実装メモ（C# 固有）
- `record` で値等価・不変・`with` コピー。Swift `struct` の写像。
- interface は **`I` プレフィックス**（C# 慣習）。Mac/Swift の "I なし" とは逆（CLAUDE.md §4-2）。
- すべての非同期 I/O は `CancellationToken` を引数で受ける（暗黙伝播はない。CLAUDE.md §3-1）。
- protocol 拡張のデフォルト実装は **拡張メソッド**で表現（後続スライスで `WaitForTrackToFinish` 等）。
- ログは `IRadioLog`（Core 抽象）+ `ConsoleRadioLog`（Infrastructure 実装）。Mac の `print()` を seam 化し、Debug はコンソールサブシステム（`OutputType=Exe`）で全ログをストリーム、Release は `WinExe`。秘密値はマスク。
