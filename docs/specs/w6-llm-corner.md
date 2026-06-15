# W6 — Gemini 台本生成 + 2-DJ トークコーナー + SongPicker

> 対応 Mac 版: `GeminiLLMBackend.swift` / `SongPicker.swift` / `DialogueScriptGenerator.swift` / `CornerEngine.swift` / `Dialogue.swift` / `LlmConfig.swift` / `DjsConfig.swift` / `CornersConfig.swift`。
> **本スライスは free_talk（2-DJ 会話 + 締めの一曲）のみ**。letter（W12）/ guest（W14）/ artist_feature（W15）、テーマプール（W12）、時報リード文・冒頭挨拶・その日の編成（W13.5）、日付/季節（W8/W17）、長期記憶（W18）は後続。

## 1. 概要
Gemini で 2 人の DJ の会話台本を生成し、締めに 1 曲かける「トークコーナー」を 1 本通す。
準備（LLM 処理・無音）と本番（発話 + 曲）の 2 段。準備で **選曲を先に確定（プレフライト, §3-2）** してから曲紹介を含む台本を生成し、
全行を事前合成して本番の TTS 待ちをゼロにする（S10）。本番は発話 → 1 曲 → 必ず pause（§3-1）。

## 2. スコープ
**in**:
- Core ドメイン型（`Dialogue.cs`）: `DjProfile`(id/name/speakerId/persona) / `DialogueLine`(djId/text) / `DialogueScript`(lines, TotalCharacters) / `CornerFormat`(free_talk/letter/guest/artist_feature) / `CornerTemplate`(id/title/theme/format/djIds/targetMinutes/charsPerMinute/songPromptHint/fallbackTrackUri/volume/playSeconds, TargetCharacters 算出)。
- Core `SongPicker`（具象, `SongRequest` + 候補パース）: LLM に「曲名 - アーティスト」を最大 5 曲挙げさせ、検索 + `IsPlayable` で最初の再生可能曲を確定。全滅は `fallbackTrackUri`（曲名不明のまま）。
- Core `DialogueScriptGenerator`（具象）: テーマ + DJ ペルソナ + 確定曲から会話台本を LLM 生成。出力契約「DJ名: セリフ」、登録 DJ 名で始まらない行は無視、最低 4 行（未満は `ScriptParseFailed`）。
- Core `CornerEngine`（具象, free_talk のみ）: `PrepareAsync`（cast 解決 → 選曲 → 台本 → 全行事前合成）→ `RunAsync`（発話 → 1 曲（`playSeconds` or `WaitForTrackToFinish`）→ pause）。`CornerEvent` で進行通知。letter/guest/artist_feature は `ConfigException`（W6 未対応）。
- Core エラー `LlmException`: KeyMissing / ApiFailed / EmptyResponse / ScriptParseFailed（`E-LLM-*`）。
- Infra `GeminiLLMBackend`（`ILLMBackend`）: `POST {endpoint}v1beta/models/{model}:generateContent`、API キーは `x-goog-api-key` ヘッダ（URL・ログに出さない）。非 2xx/解釈不能/空応答をエラー化。
- Infra `LlmConfig`（record）+ 2 ファイルローダ（`llm.yaml` 本体 + `llm.local.yaml` 機密 api_key）。local 不在 / 空 / プレースホルダ（`PASTE`）は `KeyMissing`（fail-fast）。
- Infra `DjsConfig` / `CornersConfig` ローダ（`djs.yaml` / `corners.yaml` → Core 型）。`themes`/`lead_in`/`artist_feature` は読み飛ばし（W12/W13.5/W15）。
- App デモ `corner`: 1 コーナー実走（要 Gemini キー + VOICEVOX + Premium）。
- テスト: SongPicker（候補パース・pick の preflight・全滅 fallback）/ DialogueScriptGenerator（parse・makeRequest 骨子）/ CornerEngine（prepare+run, fake 一式・pause 保証）/ Gemini（fake http: 正常/非2xx/空）/ LlmConfig（2 ファイル・keyMissing）/ Djs/Corners ローダ。

**設計判断（interface を作らない）**: `SongPicking` / `CornerRunning`（Mac protocol）は Win design.md §2 の境界リストに無い。`SongPicker` / `CornerEngine` は具象（§3-5）。`ILLMBackend` は既存境界。CornerEngine は `SongPicker`/`DialogueScriptGenerator` を内部生成する（Mac と同じ）。

**out（後続）**: letter/guest/artist_feature・themePool・leadIn・冒頭挨拶/編成・日付季節・長期記憶・番組進行（BroadcastEngine W7）・LLM ニュース会話化（W11）。

## 3. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（LLM/ネットワーク/音声非依存、fake で検証）。
- 実機: `dotnet run --project AIRadio.App -- corner` で、2-DJ の会話が生成・再生され、締めに 1 曲流れて停止する（要 `config/llm.local.yaml` の api_key + VOICEVOX + Premium + アクティブデバイス）。

## 4. 実装メモ（C# 固有 / Mac との差）
- Gemini JSON: `System.Text.Json`（`contents`/`systemInstruction`/`generationConfig.temperature`、応答 `candidates[].content.parts[].text`）。`IHttpClient.PostAsync`、`HttpStatusException` → `ApiFailed("HTTP {status}")`。
- 候補/台本パース: 行頭の箇条書き・装飾除去は `Regex`（Swift の正規表現と同パターン）。区切りは `:` / `：` 両対応。
- `LlmConfig` 2 ファイル: main（model 必須）+ local（api_key、`PASTE` 検出で KeyMissing）。`IgnoreUnmatchedProperties` で将来キーを許容。
- `CornerFormat` は YAML 文字列（`free_talk` 等）→ enum へ明示マップ（既定 free_talk）。
- 先行合成（`async let` 相当）: 準備で全行を逐次合成して `lineAudio` に格納（本番は事前合成を順次再生）。
