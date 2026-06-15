# W1 — VOICEVOX TTS + 音声再生 + 汎用 YAML ローダ

> 対応する Mac 版スライス: `s1-voicevox-tts`。挙動を C#/.NET に写像する。

## 1. 概要
DJ の声を実際に出す最初のスライス。VOICEVOX のローカル HTTP API で日本語テキストを WAV 合成し、**NAudio** で再生する。
HTTP は `IHttpClient` 抽象越しにして TTS ロジックを単体テスト可能にする。設定ロード基盤（**YamlDotNet + 汎用 `YamlConfigLoader`**）を導入し、VOICEVOX エンドポイントを `config/tts.yaml` に外部化する。

## 2. スコープ

**in**:
- 依存パッケージ **YamlDotNet** + **NAudio** 追加（Infrastructure）
- `IHttpClient` + `HttpClientAdapter`（`System.Net.Http`）+ `HttpStatusException`
- 汎用 `YamlConfigLoader`（YamlDotNet, `snake_case` = UnderscoredNamingConvention）
- `TtsConfig`（`FromYaml` / `LoadFile`、検証・クランプ）+ `config/tts.yaml`（Mac 流用）
- `VoicevoxTTS`（`ITTSBackend`, `audio_query` → `synthesis` の 2 段）
- `NAudioPlayer`（`IAudioPlayer`, WAV 再生 + 完了待ち + キャンセル即停止 + 音量クランプ）
- Core エラー: `TtsException`（unreachable / synthesisFailed）, `AudioException`（playbackFailed）
- App: **W1 デモ**（`tts.yaml` ロード → ずんだもん(3) で 1 行合成 → 再生）
- テスト: `VoicevoxTTS`（fake `IHttpClient`）/ `TtsConfig` / `NAudioPlayer`（音量・不正データ）

**out（後続）**: 複数 DJ・話者マッピング → 後続 / ダッキング・テーマ曲 → W3 / LLM 台本 → W6

## 3. VOICEVOX 連携
- `POST {endpoint}audio_query?text={正規化テキスト}&speaker={id}` → クエリ JSON
- `POST {endpoint}synthesis?speaker={id}`（body=クエリ JSON に speedScale 適用, `Content-Type: application/json`）→ WAV
- 正規化: 波ダッシュ `〜`(U+301C) / 全角チルダ `～`(U+FF5E) を長音 `ー`(U+30FC) に置換
- 失敗: 接続不可（`HttpRequestException`）→ `TtsException.Unreachable` / HTTP 異常・その他 → `SynthesisFailed`

## 4. 受け入れ条件
- `dotnet build` 成功（YamlDotNet / NAudio 解決含む）
- `dotnet test` 全グリーン（**ネットワーク・音声デバイス非依存**。VOICEVOX 実機は使わず fake で検証）
- 実機で `dotnet run --project AIRadio.App` がずんだもんの声を再生（聴覚確認、ユーザー）

## 5. 実装メモ（C# 固有 / Mac との差）
- **音声再生**: Mac は `AVAudioPlayer` + `duration+0.2s` スリープ近似。Windows は **NAudio `WaveOutEvent` + `PlaybackStopped` イベントで真の完了待ち**。キャンセルは `ct.Register(() => output.Stop())`。← 挙動差は聴覚検証（最大の技術リスク）。
- **速度適用**: `System.Text.Json` の `JsonNode` で `speedScale` を注入。
- **設定**: 汎用 `YamlConfigLoader`（`UnderscoredNamingConvention`）→ DTO → map/validate（旧版の per-file ローダ量産を避ける。CLAUDE.md §3-5）。
- `IHttpClient` は Infrastructure 内の抽象（Core は HTTP を知らない）。fake は `Infrastructure.Tests` のヘルパ。
- エラーコード（error-codes.md に起票済み）: `E-TTS-UNREACHABLE-001` / `E-TTS-SYNTHESIS-FAILED-001` / `E-RTM-AUDIO-PLAYBACK-001`
