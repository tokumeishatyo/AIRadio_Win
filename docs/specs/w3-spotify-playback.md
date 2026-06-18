# W3 — Spotify Web API 再生 + 終端検知

> 対応 Mac 版: `s4-spotify-webapi-oauth`（`WebApiSpotifyController`）+ `waitForTrackToFinish`（s10 先読み / s12 stale 耐性で堅牢化）。
> W2（PKCE 認証・検索・プレフライト）の上に **再生制御**を載せる。ダッキング演出（統一 ThemeSequencer）は W4。

## 1. 概要
Spotify Web API でローカル/任意デバイスの**再生を制御**する。`ISpotifyController`（Core, W0 で定義済み）を
`WebApiSpotifyController`（Infrastructure）として実装する。アクセストークンは `ISpotifyTokenProvider`（W2 の PKCE 認証）
から取得し、HTTP は `IHttpClient` 抽象越し。あわせて、曲セグメントが「曲を**終わりまで見届けて**戻る」ための
`WaitForTrackToFinish`（Core 拡張メソッド）と、完全静寂のための `PauseIgnoringCancellation`（Core 拡張メソッド）を移植する。

## 2. スコープ
**in**:
- `WebApiSpotifyController : ISpotifyController`（Infrastructure）
  - `PlayAsync`: 再生先デバイス解決 → `PUT /v1/me/player/play?device_id=` に `{"uris":[uri]}`（キューを当該 URI だけに置換しアトミック再生。前曲のブリップを防ぐ）。stale デバイス 404 は transfer playback で起こして再試行（最大 3 回）。
  - `PauseAsync`: `PUT /v1/me/player/pause`。**後始末（完全静寂）なので全例外を握り潰す**（既に停止済み＝403 でも結果は同じ「無音」）。
  - `SetVolumeAsync`: `PUT /v1/me/player/volume?volume_percent=`（0–100 にクランプ）。
  - `SeekAsync`: `PUT /v1/me/player/seek?position_ms=`（秒→ミリ秒、負値は 0）。
  - `PlayerStateAsync`: `GET /v1/me/player`。204/空ボディ＝アクティブデバイスなし＝Stopped。**曲長は URI・位置と同一スナップショットから返す**（別問い合わせで取り直すと直前の曲の値を掴む stale を踏む。S12 fix）。
  - `CurrentTrackDurationSecondsAsync`: 同 `GET /v1/me/player` の `item.duration_ms`。
  - デバイス選択: `preferredDeviceName` 指定時は名前一致のみ（なければ NoDevice）。未指定時は **この PC（Spotify 表示名＝`Environment.MachineName`）の Computer を最優先**し、一致が無ければ `type=Computer`（アクティブ優先、なければ最初の Computer）。**`devices.first` への安易なフォールバックはしない**（スマホ等へ転送して別の場所で鳴らす事故防止）。〔ホスト名優先は **Win 独自（Mac には無い）**。2026-06-18 W15 実機で同一アカウントの別 PC(Mac 等)へ再生される事象を受け、`device_name` 未設定でもローカル機で鳴らすために追加。一致しなければ Mac 同一の「アクティブ→先頭」ロジックに退行＝現状より悪化しない。〕
- `SpotifyControllerExtensions`（Core, `ISpotifyController` 拡張）
  - `WaitForTrackToFinishAsync`: URI 切替確認 → 残り margin 手前までチャンク寝（位置を読み直し）→ 実終端検知。終了らしき観測（停止 / 別トラック遷移 / 終端到達 / 位置停滞）は**二重確認**してから確定（stale スナップショットでの途中切り防止、S12 fix-2）。戻り値 `TrackFinishReason`（診断ログ用）。
  - `TrackFinishReason` enum: `Stopped` / `TrackChanged` / `ReachedEnd` / `PositionStalled` / `TimedOut`。
  - `PauseIgnoringCancellationAsync`: キャンセル非継承の Task で `PauseAsync(CancellationToken.None)` を送り切る。`restoringVolume` 指定時は pause 後に音量を戻す（ダッキング中の停止対策、W4 で利用）。
- `PlaybackSimulator`（TestSupport）: 仮想時間で再生位置が進む `ISpotifyController` + `IClock` 兼用 fake（終端検知テスト用）。
- App デモ `spotify-play`: 検索 → 先頭曲を `PlayAsync` → 数秒 → `PauseAsync`（要 client_id + Premium + アクティブデバイス）。
- テスト: `WebApiSpotifyControllerTests`（device 解決・ホスト名優先・非一致時フォールバック・Computer 優先・preferredDeviceName・NoDevice・volume/seek/pause・pause 例外握り潰し・playerState パース・204=Stopped・404→transfer 再試行・404 継続失敗）／ `WaitForTrackToFinishTests`（自然終端・stale 混入で切らない・確定 trackChanged・stale 曲長で早切りしない・切替直後 stale スキップ・スナップショット曲長なし時の fallback）。

**out（後続）**: 統一 ThemeSequencer（OP/news/ED の BGM ダッキング演出）→ W4 ／ ローリング先読み準備・冒頭曲セグメント → W10。

## 3. 再生フロー（PlayAsync）
1. `ValidAccessTokenAsync` でトークン取得、ボディ `{"uris":[uri]}` を作る。
2. 最大 3 回ループ:
   1. `GET /v1/me/player/devices` で再生先デバイス ID を解決（§2 のデバイス選択規則）。
   2. `PUT /v1/me/player/play?device_id=<id>` に JSON ボディ + `Authorization: Bearer`。
   3. 成功（2xx）で return。
   4. `404` かつ最終試行でない: `PUT /v1/me/player`（`{"device_ids":[id],"play":false}`）で transfer playback してデバイスを起こし、`retryDelaySeconds` 待って再試行。
   5. その他の HTTP 失敗: `SpotifyException.ApiFailed`。
3. 終了時の例外:
   - デバイス解決自体の失敗（Computer 不在 / `preferredDeviceName` 不在）→ `SpotifyException.NoDevice`。
   - play が通らないまま**最終試行も 404** だった場合 → `SpotifyException.ApiFailed`（最終試行の 404 は再試行ガード `attempt < maxAttempts` を外れ、一般失敗として送出される。Mac 版 `playGivesUpAfterPersistent404` と同一挙動）。
   - ループ後の `NoDevice` は Mac 構造に合わせた**到達不能の保険**（最終 404 は上記で送出済みのため到達しない）。

## 4. 終端検知（WaitForTrackToFinishAsync）
1. **URI 切替確認 + 同一スナップショット**: `PlayerStateAsync` を `switchPollSeconds`(0.2s) 間隔で最大 `switchMaxAttempts`(15) 回読み、`TrackUri==uri` かつ曲長>0 を確認。曲長 0 のスナップショット実装のみ `CurrentTrackDurationSecondsAsync` に倒す。
2. **チャンク寝**: 残り `marginSeconds`(5s) 手前まで、`recheckSeconds`(30s) 上限のチャンクで寝て、起きるたびに位置を読み直す（snapshot が stale でも自己修正、タイマー遅延でもデッドエアが伸びない）。`budget` は無限待ち防止の安全弁。
3. **実終端**: 停止 / 別トラック遷移 / 位置が終端到達 / 位置停滞のいずれかで戻る。
4. **二重確認（S12 fix-2）**: 「終了らしき観測」は `endPollSeconds`(0.5s) おいた再観測でも同じ結論のときだけ確定する。1 回の stale な前曲/paused スナップショットで途中切りしない。

## 5. 完全静寂（PauseIgnoringCancellationAsync）
放送停止/キャンセル/エラーの後始末 pause は、キャンセル済みフロー内では `HttpClient` がリクエストを送らず取り消すため、
**キャンセルを継承しない Task**（`Task.Run(..., CancellationToken.None)` + `CancellationToken.None`）で送り切る（CLAUDE.md §3-1）。
`restoringVolume` 指定時は pause 後にアプリ音量を戻す（ダッキング中の停止で小音量のまま残らないように）。

## 6. 受け入れ条件
- `dotnet build AIRadio.slnx` / `dotnet test AIRadio.slnx` 全グリーン（ネットワーク・Spotify 非依存、fake で検証）。
- 実機: `dotnet run --project AIRadio.App -- spotify-play` で先頭曲が実機 Spotify（Computer デバイス）で鳴り、数秒後に停止する（ユーザー確認、要 client_id + Premium + ログイン済みアクティブデバイス）。

## 7. 実装メモ（C# 固有 / Mac との差）
- 404 判定: `HttpStatusException.StatusCode == 404`（`IHttpClient` は非 2xx で `HttpStatusException` を投げる）。
- JSON: 送信は `System.Text.Json`（`{"uris":[...]}` / `{"device_ids":[...],"play":false}`）、受信は snake_case を `JsonPropertyName` で対応（`is_active` / `progress_ms` / `duration_ms`）。
- リトライ待機: Mac の `Task.sleep(retryDelaySeconds)` → `Task.Delay`（テストは `retryDelaySeconds=0`）。
- 204 No Content: `IHttpClient.GetAsync` は 2xx なので例外にならず空配列を返す → 空なら Stopped。
- actor 等価: コントローラはステートレス（トークンキャッシュは `SpotifyAuth` 側の `SemaphoreSlim`）。
- エラーコード: `E-SPT-NO-DEVICE-001` / `E-SPT-API-FAILED-001`（既起票・W0 導入）。
