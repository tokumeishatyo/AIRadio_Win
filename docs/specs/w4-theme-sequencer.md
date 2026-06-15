# W4 — 統一 ThemeSequencer（OP/news/ED の BGM ダッキング演出）

> 対応 Mac 版: `Sources/AIRadioCore/ThemeSequencer.swift` + `ThemeConfig.swift`（s11 チャンク先行合成を含む）。
> W3（Web API 再生・終端検知）の上に **BGM 演出**を載せる。OP / ニュース / ED が共有する 1 つの演出ロジック。

## 1. 概要
`tagline → BGM フル音量 → intro 待機 → ducked へ降下 → 発話 → outro へシーク → フル音量へ戻し余韻 → 停止` を
実行する統一演出。BGM は Spotify（`ISpotifyController`、W3）でアトミック再生し、発話は VOICEVOX（`ITTSBackend`）→
NAudio（`IAudioPlayer`）。長文発話は文単位チャンクに分割し、次チャンクを再生中に**先行合成**してデッドエアを排除する（S11 fix）。

## 2. スコープ
**in**:
- `ThemeConfig`（Core, record）: `Tagline?` / `TrackUri` / `IntroSeconds` / `Volume` / `DuckedVolume` / `OutroSeconds`。
- `ThemeSequencer`（Core, **具象クラス**）:
  - `RunAsync(theme, announcement, speakerId, ct)`: 演出本体 + 完全静寂の後始末。
  - `ChunkAnnouncement(text, maxChars=120)`（static）: 文末（。！？!?）区切り＋ maxChars 目安で連結したチャンク列。
- App デモ `theme`: 検索した曲を BGM に、固定 tagline + 発話で演出を実走（要 VOICEVOX + Premium + アクティブデバイス）。
- テスト（Core）: イベント順（tagline 有/無）・曲長不明時のシーク省略・チャンク分割・長文の順序保持・再生失敗時の完全静寂。

**設計判断（interface を作らない）**: Mac は `ThemeSequencing` protocol を持つが、Win の design.md §2 抽象境界リストは
これを**意図的に除外**し、§4 で `ThemeSequencer` を具象クラスとする。実差し替え境界ではなく実装は 1 個のため
interface 化しない（CLAUDE.md §3-5）。テストは依存（TTS/Audio/Spotify/Clock）を fake 注入して具象のまま検証する。
将来 `BroadcastEngine`（W7）が演出をスタブ化して進行ロジックを単体テストする必要が出た時点で seam を再検討する。

**out（後続）**:
- `themes.yaml` ローダ（OP/news/ED の track_uri・演出パラメータ・**per-DJ 固定口上**・時間帯挨拶）→ 消費者（`BroadcastEngine` W7 / 曜日替わり DJ W13.5）が現れた時点で実装。W4 ではデモ用に `ThemeConfig` をコード内で組む。
- ローリング先読み準備（演出セグメントの 2 つ先準備）→ W10。
- ニュース原稿の LLM 会話化 → W11。

## 3. 演出シーケンス（RunAsync）
1. `tagline` が非 null/非空なら合成して再生（ED は null = いきなり BGM）。
2. `announcement` をチャンク分割。BGM を `PlayAsync(TrackUri)` で再生 → `SetVolume(Volume)`（フル音量イントロ）。
3. 先頭チャンクを**イントロ中に先行合成**: 合成 Task を起動 → `Delay(IntroSeconds)` → `SetVolume(DuckedVolume)`（ダッキング）→ 合成結果を await。
4. チャンクを順次再生。次チャンクは現在チャンク再生中に先行合成（無音排除、§design async let プリフェッチ）。
5. アウトロ: 曲長を取得（失敗は握り潰し＝シークしない）。曲長 > `OutroSeconds` なら「残り `OutroSeconds` 秒」へ `SeekAsync`。`SetVolume(Volume)`（アンダック）→ `Delay(OutroSeconds)` で余韻。
6. 正常終了で `PauseAsync`。**例外/キャンセル時**は `PauseIgnoringCancellationAsync(restoringVolume: Volume)`（完全静寂・音量復元、§3-1）後に再 throw。

## 4. 受け入れ条件
- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン（ネットワーク・音声非依存、fake で検証）。
- 実機: `dotnet run --project AIRadio.App -- theme` で tagline 発話 → BGM フル音量 → ダッキングして発話 → アウトロでフル音量に戻り曲尾で停止、までが聞こえる（ユーザー聴覚確認、要 VOICEVOX 起動 + Premium + アクティブデバイス）。

## 5. 実装メモ（C# 固有 / Mac との差）
- `async let` プリフェッチ → 合成 `Task` を await せず保持し、後で await（hot task）。`design.md` §2 の写像通り。
- チャンク文字数: Swift `String.count`（書記素）→ C# `string.Length`（UTF-16 コードユニット）。本番文言（BMP 日本語）では一致。
- `try?`（曲長取得）→ `try { ... } catch { duration = 0; }`（曲長不明ならシーク省略）。直後の `SetVolume`/`Delay` がキャンセルを伝播。
- `pauseIgnoringCancellation(restoringVolume:)` は W3 で移植済みの Core 拡張を利用。
- `ThemeConfig` は `record`（値等価・`with`）。news が tagline を後付けする Mac の可変 struct は、Win では `with` で表現可能（後続スライス）。
