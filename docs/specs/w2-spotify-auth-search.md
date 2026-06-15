# W2 — Spotify PKCE 認証基盤 + 検索 / プレフライト

> 対応 Mac 版スライス: `s2-spotify-hybrid` + `s4-spotify-webapi-oauth`。
> Mac の現行コードは検索・再生とも **OAuth PKCE のユーザートークンに一本化**済み（S2 の Client Credentials は S4 で廃止）。
> よって Windows では Client Credentials 中間形は作らず、**PKCE 認証 → 検索/プレフライト**を W2 にまとめる。

## 1. 概要
Spotify を扱う土台。**Authorization Code + PKCE**（公開クライアント、client_secret 不要）でユーザートークンを取得し、
Web API で曲検索・再生可否（プレフライト）を行う。トークンは DPAPI で暗号化保管し、refresh で無音更新する。
HTTP は `IHttpClient` 抽象越し。再生（playback）は W3、ダッキング演出は W4。

## 2. スコープ
**in**:
- パッケージ `System.Security.Cryptography.ProtectedData` 追加（Infrastructure）
- `SpotifyConfig`（client_id / redirect_uri / market / device_name、loopbackPort 導出）+ `config/spotify.local.yaml.sample`
- `Pkce`（verifier=`RandomNumberGenerator`、challenge=`SHA256`、base64url）
- `ISpotifyTokenProvider` + `SpotifyAuth`（PKCE 認可・トークン交換/更新・キャッシュ。`SemaphoreSlim` ガード）
- `ITokenStore` + `DpapiTokenStore`（`ProtectedData`, `%LOCALAPPDATA%\AIRadio\spotify.token`）
- `LoopbackServer`（`System.Net.HttpListener`, `127.0.0.1:<port>/callback` で認可コード受領）
- `SpotifyWebSearcher`（`ITrackSearcher`: search / isPlayable、ユーザートークン利用）
- `SpotifyUri`（Core: track ID 抽出）
- Core エラー `SpotifyException` に AuthFailed / AuthRequired / SearchFailed 追加
- App デモ: `spotify-auth`（ブラウザログイン→refresh 保管）/ `spotify`（検索→プレフライト表示）
- テスト: Pkce(RFC7636) / SpotifyConfig / SpotifyAuth(更新・キャッシュ・authRequired・formEncode) / SpotifyWebSearcher / DpapiTokenStore(往復) / SpotifyUri

**out（後続）**: Web API 再生（play/pause/volume/seek/state）→ W3 ／ 統一 ThemeSequencer → W4 ／ LLM 選曲ヒント → W6。

## 3. 認証フロー（PKCE）
1. verifier 生成 → challenge = base64url(SHA256(verifier))
2. ブラウザで `accounts.spotify.com/authorize`（client_id / response_type=code / redirect_uri / code_challenge_method=S256 / code_challenge / scope）
3. `LoopbackServer` が `http://127.0.0.1:5543/callback?code=...` を受領
4. `POST /api/token`（grant_type=authorization_code, code, redirect_uri, client_id, code_verifier）→ access/refresh
5. refresh を DPAPI 保管。以降は refresh_token で無音更新（期限 -30s でキャッシュ）

## 4. Web API（検索・プレフライト）
- 検索: `GET /v1/search?q=&type=track&limit=&market=`（`Authorization: Bearer`）
- 再生可否: `GET /v1/tracks/{id}?market=` の `is_playable`

## 5. 受け入れ条件
- `dotnet build` / `dotnet test` 全グリーン（ネットワーク・Spotify 非依存、fake で検証。DPAPI 往復は temp ファイル）
- 実機: `dotnet run --project AIRadio.App -- spotify-auth` でログイン成功 → `-- spotify` で検索結果＋プレフライト表示（ユーザー確認、要 client_id + Premium）

## 6. 実装メモ（C# 固有 / Mac との差）
- 秘密保管: Keychain → **DPAPI**（`ProtectedData.Protect/Unprotect`, CurrentUser, `%LOCALAPPDATA%`）。
- ループバック: Network.framework → **`HttpListener`**（`QueryString["code"]`）。
- ブラウザ起動: `NSWorkspace.open` → `Process.Start(UseShellExecute=true)`。
- actor → `SemaphoreSlim` ガードのトークンキャッシュ。
- client_id は `config/spotify.local.yaml`（gitignore、Mac と同方式）。client_secret は不要。
- エラーコード: `E-SPT-AUTH-FAILED-001` / `E-SPT-AUTH-REQUIRED-001` / `E-SPT-SEARCH-FAILED-001`（error-codes.md 起票済み）。
