# W19a — 読み正確化①：VOICEVOX ユーザー辞書の冪等同期（コア）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。挙動の正は **Mac 版 shipped コード**（`../AIRadio_Mac-main/`, s19a）。
> 要件（読み正確化）の採用分の**前半**。アーティスト読みの自動登録は **W19b**（別スライス）に分割（§12）。
> 参照: `w18-station-journal.md`（fail-tolerant 配線の前例）, `error-codes.md`（PRON カテゴリ）。
> **実機検証根拠（Mac）**: VOICEVOX **v0.25.2**（`http://127.0.0.1:50021`）で `/user_dict` API と辞書の効き方を実証済み（§4・§5・§11）。

## 1. 概要

固有名詞・難読語の**誤読を矯正**する。`config/pronunciations.yaml`（表記 → カタカナ読み ＋任意アクセント）を
**放送開始時に VOICEVOX のユーザー辞書 `/user_dict` へ冪等同期**する。VOICEVOX は登録済み表記を入力テキスト内で
**形態素解析で検出**すると、指定の読み・アクセントで発音する（辞書は VOICEVOX エンジンのグローバル状態）。

- ✅ 効く例（Mac 実機確認済み）: `栄光の架橋 → エイコウノカケハシ` / `お家 → オウチ` / `Mr.Children → ミスターチルドレン` / `Official髭男dism → オフィシャルヒゲダンディズム` / `BUMP → バンプ`。
- **全ひらがな化はしない**（カタカナ読み＋`accent_type` でピッチを保持。ひらがな化はアクセント情報を失い棒読みになる）。
- 既存の文字レベル正規化 `VoicevoxTTS.NormalizeForSpeech`（波ダッシュ／全角チルダ → 長音）と**役割分担**:
  正規化＝文字単位の置換（合成のたびにテキストを munge）／辞書＝語単位の読み・アクセント矯正（VOICEVOX 側の状態）。両者は補完関係。

### 1.1 既知の制約（Mac 実機確定・仕様として明記）

VOICEVOX のユーザー辞書は**形態素解析で surface を照合**する。**空白を含む純英字の surface は単一トークンとして照合されず、登録しても効かない**。

- ❌ 効かない: `SEKAI NO OWARI`（登録しても `エス・イー・ケー…` とアルファベットを 1 文字ずつ棒読み）、`BUMP OF CHICKEN` 等。
- ✅ 効く表記クラス: 日本語（`栄光の架橋`）、漢字交じり、**空白なし**英字単語（`BUMP`）、記号で結合した英字（`Mr.Children`・`Official髭男dism`）。
- **運用での回避（ユーザー判断 2026-06-14：今は割り切り）**: 空白英字名は**通称カナ**（`セカオワ`）や**空白を詰めた表記**を surface にする。
  根本対応（合成前のテキスト置換層）は将来の検討課題として §12 にメモ（今スライス対象外）。

## 2. データモデル（Core）

`ArtistProfile`（`ArtistFeature.cs`）に倣い、ドメイン型は **Core**・ローダ／同期実装は **Infrastructure**。

```csharp
// AIRadio.Core/Pronunciation.cs
public sealed record PronunciationEntry(
    string Surface,          // 表記（入力テキストにこの語が現れたら読みを差し替える）
    string Pronunciation,    // 読み（全角カタカナ。§4 の制約参照）
    int AccentType = 0,      // アクセント型（音が下がるモーラ位置。0 = 平板。既定 0）
    string? WordType = null, // 任意。PROPER_NOUN / COMMON_NOUN / VERB / ADJECTIVE / SUFFIX
    int? Priority = null);   // 任意。0–10（大きいほど優先・未指定時サーバ既定 5）
```

- `AccentType` の既定は **0**（ユーザー判断 2026-06-14）。**API では必須**なので、yaml 未指定でもクライアントが常に 0 を送る（§4）。
- `Pronunciation` は **全角カタカナ必須**（ひらがな・半角カナは VOICEVOX が 422 で弾く。§4・§5 で送信前に正規化／検証）。

## 3. 設定ファイル（`config/pronunciations.yaml`）

```yaml
# 読み正確化（仕様 w19a）。表記 → カタカナ読み。放送開始時に VOICEVOX /user_dict へ冪等同期する。
pronunciations:
  - surface: "栄光の架橋"
    pronunciation: "エイコウノカケハシ"
  - surface: "Mr.Children"
    pronunciation: "ミスターチルドレン"
  - surface: "Official髭男dism"
    pronunciation: "オフィシャルヒゲダンディズム"
```

- 命名規約（CLAUDE.md §3-4）: kebab-case ファイル / snake_case キー。機密ではないので `.local` ではなく**コミット対象**（サンプル兼デフォルト辞書）。Mac shipped と同一 3 エントリを出荷。
- **手編集前提**。行を足せば次の放送から反映（既存「YAML は放送のたびに読み直す」方針と整合）。
- **出荷デフォルトに入れるのは Mac 実機で効くと確認済みのエントリのみ**（§1.1 の効く表記クラス）。**空白英字名は入れない**。
- `artists.yaml` は出荷時空・このファイルだけがデフォルト辞書を持つ。
- `AIRadio.App.csproj` の `Content Include="..\config\**\*.yaml"` で出力ディレクトリに自動コピーされる（追加配線不要）。

## 4. VOICEVOX 辞書 API（Mac 実機 v0.25.2 確定）

| 操作 | メソッド・パス | パラメータ | 備考 |
|---|---|---|---|
| 取得 | `GET /user_dict` | なし | 応答 = `{ uuid文字列: UserDictWord }`。初期 `{}`。正常時 200（空でも `{}`） |
| 追加 | `POST /user_dict_word` | **クエリ**: `surface`✱ / `pronunciation`✱ / `accent_type`(int)✱ / `word_type`? / `priority`(0–10)? | **body=null**。成功 **200**、応答 = 生成 UUID（`"..."` 二重引用符付き文字列） |
| 更新 | `PUT /user_dict_word/{uuid}` | path: `uuid`✱ ＋ 追加と同じクエリ | uuid 不在は **422**（404 ではない） |
| 削除 | `DELETE /user_dict_word/{uuid}` | path: `uuid`✱ | **本スライスでは使わない**（§6） |

✱ = **API 必須**。重要な実機事実（仕様の前提）:

1. **登録はクエリパラメータ方式**（JSON body ではない。`audio_query` と同じ「クエリのみ・body=null の POST」流儀＝`VoicevoxTTS` と同形）。日本語値は URL エンコード必須（`Uri.EscapeDataString`）。
2. **`accent_type` はサーバ必須・既定なし** → yaml 未指定時もクライアントが**常に 0 を送る**（送らないと 422）。`accent_type=0`（平板）は実機で常に妥当（範囲 0..モーラ数）。
3. **`pronunciation` は全角カタカナ限定** → ひらがな・**半角カナ**は 422（「発音は有効なカタカナでなくてはいけません」）。送信前に NFKC で全角カタカナへ正規化＋カタカナ検証（§5）。全角カタカナは入力どおり round-trip して返る（＝読み比較が安定・PUT 無限ループの懸念なし）。
4. **サーバが `surface` を全角に正規化して保存・返却する** → `Mr.Children` は GET で `Ｍｒ．Ｃｈｉｌｄｒｅｎ`（英字は全角・空白は半角のまま）。**raw 文字列での突合は不可**。突合は NFKC 正規化後の surface で行う（§5）。
5. `priority` 未指定時のサーバ既定は **5**。`word_type` 未指定は part_of_speech=名詞 として登録。**差分判定には priority/word_type を含めない**（含めると既定 5 とのズレで誤 PUT が起きる）。
6. POST 応答 UUID（二重引用符付き）は**本スライスでは未使用**（PUT 対象 uuid は GET から取る）。

## 5. 辞書同期（Infrastructure）

```csharp
// AIRadio.Infrastructure/Tts/VoicevoxUserDict.cs
public sealed class VoicevoxUserDict
{
    public VoicevoxUserDict(string endpoint, IHttpClient http);  // VoicevoxTTS と同じ base パース・http 共有

    /// 冪等同期：表記が無ければ追加・読み/アクセントが違えば更新・同一なら何もしない。削除はしない。
    /// 完全 fail-tolerant：throw しない（VOICEVOX 未起動・個別 422 でも放送を止めない）。
    public Task<PronunciationSyncSummary> SyncAsync(
        IReadOnlyList<PronunciationEntry> entries, CancellationToken ct = default);
}

public record struct PronunciationSyncSummary(   // mutable（Mac の値型 struct と同じく集計で書き換える）
    int Added = 0, int Updated = 0, int Skipped = 0, int Failed = 0, bool Unreachable = false);
```

- **新 interface は作らない**（`VoicevoxUserDict` は具象クラス。外部依存は既存 `IHttpClient` シーム越し＝§3-5）。Mac の struct + protocol なし に対応。

### 5.1 正規化（実機の癖を吸収。突合・冪等の要）

- **突合キー** `MatchKey(surface) = NFKC(surface)`。config の raw surface と GET 返却の全角 surface を**両辺とも NFKC**して比較する（`Mr.Children` ↔ `Ｍｒ．Ｃｈｉｌｄｒｅｎ` を一致させる）。これが無いと英字エントリが毎回 POST されて二重登録になる（§4-4）。なお `NFKC(surface)` は**突合キー計算専用**で、POST/PUT の送信 surface には使わない（Mac `wordURL` と同じく config の **raw surface をそのまま送る**。サーバが全角再正規化するため最終状態は同一＝§13 W-7）。
- **読みの正規化** `NormalizePronunciation(pron) = NFKC(pron)`（半角カナ → 全角カナ）。さらに**カタカナ検証**（カタカナ U+30A1–U+30FA ＋ 長音 `ー` U+30FC ＋ 中黒 `・` U+30FB のみ許容）。**非カタカナ（ひらがな・漢字・英字等）を含むエントリは送信せずスキップ**し `Failed++`＋`E-PRON-WORD-REJECTED-001` をログ（422 を取りに行かない）。**空文字列も非カタカナ扱い（`Failed`）**とする（Mac `isKatakana` の `!text.isEmpty &&` を写す。C# の `Enumerable.All` は**空シーケンスで true** を返すため、`!string.IsNullOrEmpty(pron) && pron.All(c => …)` のように**非空ガードを前置**する。なお正規の配線ではローダが空 pronunciation を `ConfigException` で fail-fast 排除するため到達不能＝この注記は helper 単体契約の忠実性のため）。
- **config 内の重複**は `MatchKey` で先勝ち排除（表記ゆれ `Mr.Children` / `Ｍｒ．…` の二重処理を防ぐ）。
- **NFKC は .NET 単一呼び出し**: `text.Normalize(NormalizationForm.FormKC)`。Mac は `precomposedStringWithCompatibilityMapping` が結合用濁点（U+3099）を残すため `precomposedStringWithCanonicalMapping` を重ねて 2 段で「ﾄﾞ → ド」を合成するが、.NET の `FormKC`（互換分解＋正準合成）は 1 呼び出しで合成まで行う＝等価（§13 W-1）。

### 5.2 同期アルゴリズム（冪等・no-throw）

1. **`if (ct.IsCancellationRequested) return summary;`**（GET 前に観測。停止後は外部 I/O を発行しない＝CLAUDE.md §3-1。Win 固有の早期ガード＝§13 W-2）。
2. **`if (entries.Count == 0) return summary;`**（GET 前。Mac `guard !entries.isEmpty else { return summary }`〔VoicevoxUserDict.swift:41〕の写し。**空入力では GET すら発行しない**＝空サマリ。§9 の「空リスト → GET 呼ばない」受け入れ条件に対応）。**空ガードと ct 早期ガードはともに GET 前**（順序契約。Mac は空ガードのみが最初、ct はループ内）。
3. `GET /user_dict` → `Dictionary<string, ExistingWord>` をデコード（`ExistingWord { Surface; Pronunciation; AccentType }`、`accent_type` を `JsonPropertyName` で対応）。
   - **GET が失敗したら** `Unreachable = true` にして即 return（`E-PRON-SYNC-UNREACHABLE-001` をログ・**放送は継続**）。失敗は (a) HTTP 層（`HttpStatusException` 非 2xx / `HttpRequestException` 接続失敗）と (b) **GET 成功（200）後のデコード失敗（応答破損）** の両方を含み、**GET 呼び出しとデコードを同一 `try` で囲い `catch (Exception)` で全捕捉**（Mac の bare catch〔VoicevoxUserDict.swift:45-50、`http.get` と `JSONDecoder().decode` を 1 つの do/catch で覆う〕を再現）。
4. `MatchKey(GET surface) → (uuid, ExistingWord)` のマップを構築（**raw な `ExistingWord` をそのまま保持**・正規化しない。Mac `bySurface[matchKey(word.surface)] = (uuid, word)` 相当。読みの正規化は step 5 の比較時に 1 回だけ行う）。
5. 各 `entry` について:
   - **ループ先頭で `if (ct.IsCancellationRequested) return summary;`**（throw しない cancellation 観測。Mac `Task.isCancelled`〔VoicevoxUserDict.swift:62-64〕相当。GET 成功後・書き込み前のキャンセルでも以降の POST/PUT を発行しない＝§3-1 の sync 区間版）。
   - `key = MatchKey(entry.Surface)`、**config 内 `key` 重複は素 `continue` で先勝ち排除**（`Failed`/`Skipped` を**増やさない**＝Mac `guard seen.insert(key).inserted else { continue }` の bare continue と同義）。**この重複判定はカタカナ検証より前**（dedup-before-validation。捨てた重複の読みが非カタカナでも `Failed` にしない）。
   - 読みを `NormalizePronunciation` で正規化＋カタカナ検証。非カタカナ → `Failed++`（`E-PRON-WORD-REJECTED-001`）・continue。
   - 既存に無い → `POST /user_dict_word`（クエリに **surface（＝config の raw 値そのまま・NFKC しない）** / pronunciation（＝正規化後）/ accent_type［＋ word_type/priority があれば］、body=null）→ `Added++`。
   - 既存にあり **`NormalizePronunciation(existing.Pronunciation) != pron` または `existing.AccentType != entry.AccentType`** → `PUT /user_dict_word/{uuid}`（同クエリ。surface は raw）→ `Updated++`。比較は **読みと accent_type のみ**（priority/word_type は対象外＝§4-5）。
   - 既存にあり、読みもアクセントも同一 → **何もしない** → `Skipped++`。
   - 個別の POST/PUT 失敗は **全捕捉**（`catch (Exception)`＝422/通信失敗/その他）→ そのエントリのみ `Failed++`＋`E-PRON-WORD-REJECTED-001` ログ、**ループは継続**（throw しない）。
6. `PronunciationSyncSummary` を返す（呼び出し側がログ）。

- **再実行で何も起きない**（同一なら skip）＝真に冪等。**手動登録分は触らない**（自分の config に無い surface は無視）。
- 比較は **Pronunciation と AccentType のみ**（§4-5。priority/word_type は含めない）。
- 登録/更新は逐次。初回は N 件の POST が走るが、ローカル HTTP かつ「放送開始前の無音は許容」原則で許容範囲。冪等が効けば再放送時は skip のみ。
- **TOCTOU 補足**: GET→個別 POST/PUT の read-modify-write だが、多重放送を `BroadcastSession`（`Idle` ガード）が拒否するため非問題。

## 6. 削除しない設計（IHttpClient に DELETE が無い／要件とも一致）

- 既存 `IHttpClient`（Get/Post/Put のみ）を拡張しない。同期は**追加・更新のみ・削除なし**。
- これは「**ユーザー手動登録分を触らない**」要件と一致する正しい意味論。
- **既知の割り切り**: `pronunciations.yaml` から行を消しても、すでに VOICEVOX に載った読みは残る。surface を書き換えた場合（例: 効かない `SEKAI NO OWARI` → 効く `セカオワ`）、**旧エントリは VOICEVOX 側に残留**する。気になる場合は VOICEVOX のユーザー辞書画面で削除する（運用導線）。将来 DELETE 対応するなら `IHttpClient` に追加（本スライス対象外）。

## 7. 配線

- `BroadcastComposition.RunAsync`（`Composition/BroadcastComposition.cs`。Mac `makeBroadcastStack`＋`BroadcastStack.run()` 相当。**本番＝トレイと `broadcast` デモが共用**）で:
  - `config/pronunciations.yaml` をロード（無ければ空＝同期なし。壊れていれば throw＝fail-fast `ConfigException`）。
  - `var userDict = new VoicevoxUserDict(ttsConfig.Endpoint, http);`（`VoicevoxTTS` と同じ `http`・endpoint を共有）。
  - **`await engine.RunAsync(...)` の直前**に `var summary = await userDict.SyncAsync(pronunciations, ct);` → サマリをログ。
  - **本番もデモも `RunAsync` 経由なので両放送経路をカバー**。単機能デモ（`tts`/`corner`/`theme`/`news`）は `RunAsync` を通らず同期しない（開発用ツールのため許容）。
- W19a では **`pronunciations.yaml` のみ**を同期する（`artists.yaml` の reading 統合＝Mac `mergedEntries` は **W19b**。§12）。
- **完全 fail-tolerant**: `SyncAsync` は throw しない。VOICEVOX 未起動でも `Unreachable` で続行（合成時に既存の `E-TTS-UNREACHABLE-001` が出る）。
- ログ整形（Mac `logPronunciationSync` 相当）: `Unreachable` → `E-PRON-SYNC-UNREACHABLE-001` 行。それ以外で `Added+Updated+Failed>0` のときのみ「追加 N 更新 N skip N」を出し、`Failed>0` なら `E-PRON-WORD-REJECTED-001` を付記。変更なし（skip のみ）は無言。

## 8. エラーコード（PRON カテゴリ・既出）

`error-codes.md` と CLAUDE.md §4-3 に `PRON` は **W18 時点で記載済み**（追記不要）。**ART/JNL と同じく診断ログ用・throw しない**（同期は fail-tolerant）。設定破損・必須欠落は従来どおり CFG（`E-CFG-MISSING-FIELD-001`、fail-fast）。

| コード | カテゴリ | 発生条件 |
|---|---|---|
| `E-PRON-SYNC-UNREACHABLE-001` | PRON | VOICEVOX に接続できず辞書同期できない（GET 失敗。ログのみ・放送継続） |
| `E-PRON-WORD-REJECTED-001` | PRON | 個別エントリの登録/更新が拒否（422 等）または読みが非カタカナ → そのエントリのみスキップ（ログのみ） |

**PRON コードの置き場所は App 層**（`BroadcastComposition` のログ整形）。リテラル、または App-local の定数（例: `BroadcastComposition` 内 `private const string`）で持ち、**Core には新設しない**。根拠:
- **ART は Core が消費者**（`ArtistFeature.cs` が `ArtistFeatureErrors.EmptyPoolReason()` 等を呼び reason 文字列にコードを埋め `FeatureSkipped` で App へ渡す）ため Core に定数クラスを置く。**PRON は `SyncAsync` が件数のみ返し**、コード文字列を必要とするのは App 層のログ整形だけ（Mac も `logPronunciationSync`〔`BroadcastWiring.swift:70-79`、App 層〕にリテラル直書きで、Infra `VoicevoxUserDict` はコードを持たない）。
- **JNL（W18）も診断コードを Core/App いずれにも定数化せず**ログのリテラルのみ（`error-codes.md`）。Core に消費者のいない `PronunciationErrors` を新設すると §3-5（実装 1 個限りの抽象を作らない）に逆行する。
- `RadioException` 派生は作らない（**throw しない**）方針は維持。

## 9. テスト（`dotnet test AIRadio.slnx` グリーンが完了条件）

`FakeHttpClient`（`Infrastructure.Tests/Helpers/FakeHttpClient.cs`、`Func<Uri, byte[]> responder`・`Requests` 記録）で決定論検証。`url.AbsolutePath.Contains("user_dict")` でエンドポイント分岐。responder から throw（`HttpStatusException(422)`/`HttpRequestException`/`Exception`）で失敗を注入。

- **`PronunciationsConfig`**: surface/pronunciation/accent_type/word_type/priority をパース／`accent_type` 欠落で既定 0／ファイル無しで空／空・コメントのみで空／surface 欠落・pronunciation 欠落で `ConfigException.MissingField` throw／壊れ YAML で throw。
- **`VoicevoxUserDict.SyncAsync`**:
  - 空辞書（GET `{}`）＋2 件 → `POST /user_dict_word` が 2 回、クエリに surface/pronunciation/accent_type（URL エンコード）が載る。`accent_type` は未指定でも `0` が載る。**POST クエリの surface は config の raw 値（例: 半角 `Mr.Children`）がそのまま載る**（正規化済みの全角ではない＝§13 W-7）。
  - **正規化突合（冪等の核）**: GET が全角 surface（`Ｍｒ．Ｃｈｉｌｄｒｅｎ`）＋全角カタカナ読みを返し、config が半角 surface（`Mr.Children`）＋同一読み → **POST も PUT も呼ばれず `Skipped`**。
  - 既存と読み違い → その uuid へ `PUT`（`Updated`）。priority だけ違っても PUT しない（比較対象外）。accent_type 違いでも `PUT`。
  - **半角カナ読み**（`ﾐｽﾀｰﾁﾙﾄﾞﾚﾝ`）→ NFKC で全角化して POST のクエリに全角カタカナが載る。**ひらがな読み**（`みすたー`）→ 送信されず `Failed`（書き込み 0 件）。
  - 個別 `422`／`HttpRequestException`／その他 → そのエントリのみ `Failed`、他は処理継続、**sync は throw しない**。
  - `GET /user_dict` が例外（接続失敗）→ `Unreachable = true`・以降の書き込みなし。`GET` が `HttpStatusException(500)` → 同じく `Unreachable`。
  - **GET 成功（200）後のデコード失敗**: responder が throw せず**壊れた本文**（JSON オブジェクトでない byte[]＝配列/非オブジェクト/途中切れ）を返す → `Unreachable = true`・書き込みなし・`Requests` は GET 1 件のみ。デコードが GET と同一 `try` 内にあること（Mac parity）を強制する。※ Mac s19a §9 自体はこのケースを列挙しないため、これは **Mac より厳格化**であり parity 逸脱ではない。
  - `word_type`/`priority` は指定時のみクエリに載る／未指定なら載らない。
  - **config 内 surface 重複**（`Mr.Children` と全角同義）→ 後者は無視（POST 1 回）。かつ **`Skipped == 0 && Failed == 0`**（捨てた重複は素 `continue` でカウンタ不変＝Mac `seen.insert.inserted`）。
  - **dedup-before-validation の順序**: 重複した 2 件目の読みを非カタカナ（ひらがな）にしても、重複排除がカタカナ検証より先に効くため `Failed` にならず POST は 1 回のまま（`Failed == 0`）。
  - 空リスト入力 → GET すら呼ばない・空サマリ（Added=Updated=Skipped=Failed=0・Unreachable=false・`Requests` 空。§5.2 step 2）。
  - cancellation (a) **同期前**にキャンセル → GET も書き込みも発生しない（`Requests` が空。早期ガード §5.2 step 1 / §13 W-2）。
  - cancellation (b) **GET 成功後**にキャンセル（responder が `{}` を返しつつ内部で `cts.Cancel()` を呼ぶ。`FakeHttpClient` は ct 非尊重・responder 同期実行のため決定論）→ entry を渡しても POST/PUT が 0（`Requests` は GET のみ）・空サマリ。ループ先頭ガード（§5.2 step 5・Mac `Task.isCancelled` 相当）を覆う。
- **配線（任意・最小）**: `pronunciations.yaml` 無しで sync 入力が空・放送は通常進行（既存テストに無影響）。

## 10. 受け入れ基準（ライブ確認・表記クラス別。実機は最後に一括）

1. **日本語/和語が効く**: `栄光の架橋 → エイコウノカケハシ`、`お家 → オウチ` を入れて放送 → 正しい読みで発音。
2. **記号交じり英字が効く**: `Mr.Children → ミスターチルドレン`、`Official髭男dism → オフィシャルヒゲダンディズム` が正しく発音。
3. **空白純英字は効かない＝仕様**: `SEKAI NO OWARI` は登録しても効かない（§1.1）。これを**バグでなく既知制約**として確認し、通称カナ運用で回避できることを確認。
4. **冪等**: 同じ語で再放送 → 二重登録されない（`GET /user_dict` のエントリ数が増えない）。※同一プロセス内・外部から辞書を触らない条件で。
5. **VOICEVOX 落ち**: VOICEVOX を止めて放送開始 → 辞書同期は失敗ログ（`E-PRON-SYNC-UNREACHABLE-001`）のみで、放送自体は通常どおり進む（その後 VOICEVOX を上げれば合成は復帰）。
6. **削除しない割り切り**: `pronunciations.yaml` から行を消しても、その回は読みが残る（§6）。

## 11. スコープ外（W19a）

- **アーティスト読みの自動登録 → W19b**（§12。`ArtistProfile.Reading` 追加・`ArtistsConfig` の reading 読み・`ArtistListGenerator` 出力契約変更・`MergedEntries`）。
- 辞書エントリの削除・全置換（`import_user_dict`）。`IHttpClient` への DELETE 追加。
- 合成前テキスト置換層（空白英字名の根本対応。§12 にメモ）。
- 単機能デモ（tts/corner/theme/news）への辞書同期。アクセント自動推定。読みの自動学習。複数 VOICEVOX エンジン対応。

## 12. W19b（次スライス）への申し送り — アーティスト読み自動登録

ユーザー判断（2026-06-14）でスコープには含むが、**実装は別スライス**（破壊的変更＋別軸検証のため）。W19b で専用 spec を起こす。設計メモ:

- **Core**: `ArtistProfile`（`ArtistFeature.cs`）に `Reading: string?`（既定 null・後方互換）を追加。
- **Infrastructure**: `ArtistsConfig.ArtistDto` に `Reading`／`ArtistListGenerator` の出力契約変更（名前のみ → `アーティスト名<TAB>カタカナ読み`）。
  - **parseEntries の逸脱耐性を仕様化（必須）**: ①タブ無し行は「名前のみ・reading=null」で必ず救済 ②最初のタブのみで 2 分割・以降無視 ③reading が非カタカナ（ひらがな/漢字/英字/「不明」等）なら null 扱いで捨てる（§5.1 のカタカナ検証を流用）。
- **辞書反映**: `artists.yaml` の `reading` を §5 の同期に合流（`MergedEntries(pronunciations, artists)`＝Mac `VoicevoxUserDict.mergedEntries`、pronunciations.yaml が surface 衝突で優先）。Mac shipped はこの merged を `BroadcastStack.run()` で同期するが、Win は W19a では `pronunciations` のみ同期し、merged は W19b で導入する。
- **空白英字バンド名の壁**: §1.1 の制約により `SEKAI NO OWARI` 等の自動登録は効かない。W19b でも「今は割り切り・通称カナ運用」を踏襲。根本対応（合成前テキスト置換層）は W19b 以降の検討課題。
- **破壊的変更の隔離**: W19b は既存「アーティスト一覧を生成」ボタンの挙動を変えるため、独立スライス＋独立ライブ確認で退行検証とロールバックを容易にする。

## 13. Win 固有 parity 注記（確定）

- **W-1. NFKC は 1 呼び出し**: `text.Normalize(NormalizationForm.FormKC)`。Mac の 2 段（compatibility→canonical）は .NET の `FormKC` 1 回で等価（互換分解＋正準合成。半角濁点も合成）。本スライス唯一の実装差分（挙動は同一）。
- **W-2. cancellation の早期ガード**: `SyncAsync` は GET 発行**前**にも `ct.IsCancellationRequested` を観測する（Mac はループ内のみ＝GET の bare catch が mid-GET cancellation を `unreachable` に飲む）。Win は明示 ct スレッディングのため、事前キャンセルで GET すら発行せず空サマリを返す（記録型 `FakeHttpClient` 前提で §9 の「Requests が空」を決定論化）。**ただし Mac 相当のループ先頭ガード（§5.2 step 5）も残す**（GET 成功後・書き込み前のキャンセルを止める core 挙動）。**空ガード（§5.2 step 2）と ct 早期ガード（step 1）はともに GET 前**。実アプリでは sync 直後の `engine.RunAsync(ct)` が同じ ct を即時尊重するため挙動等価。
- **W-7. 送信値: surface=raw / pronunciation=NFKC 後**（Mac `wordURL` parity）。`NFKC(surface)` は `MatchKey`（突合キー）計算専用で、POST/PUT のクエリ surface には config の raw 値をそのまま載せる（サーバが全角再正規化するため最終状態は同一）。正規化するのは pronunciation のみ。
- **W-8. GET 本文の裸 `null` も破損扱い**: `JsonSerializer.Deserialize<Dictionary<…>>` は本文が裸 `null` のとき throw せず `null` を返すため、`?? throw new JsonException(…)` で破損として `Unreachable` に落とす（Mac の非 Optional 辞書デコードは `null` で throw → unreachable と一致）。`{}` は空辞書、配列/途中切れは `JsonException` で、いずれも全捕捉。実機 VOICEVOX は常に `{}` を返すため到達不能だが厳密 parity のため明示。
- **W-3. fail-tolerant の全捕捉**: GET も個別 POST/PUT も `catch (Exception)`（Mac の Swift `catch {}` bare catch 相当）。OCE も含めて飲む＝`SyncAsync` は決して throw しない。これは「放送 1 本を 1 Task で回し停止は ct」の §3-1 とは別レイヤ（sync は放送開始**前**の診断ステップで、停止は直後の `engine.RunAsync` が担う）。
- **W-4. `PronunciationSyncSummary` は（可変）`record struct`**（Mac の可変値型 struct に対応・`Equatable` は record で自動）。単一ローカル `summary` を `summary.Added += 1` で積み上げて値返しする（Mac の `var summary; summary.added += 1` と同義・コピー安全）。
- **W-5. `VoicevoxUserDict` は具象クラス・新 interface なし**（§3-5）。`IHttpClient`（既存シーム）越しに HTTP。`PronunciationsConfig` は `ArtistsConfig` 形（static class + `YamlConfigLoader.Deserialize<Dto>` + map/validate、欠落は `ConfigException.MissingField`、ファイル無し・空・コメントのみは空リスト）。
- **W-6. mergedEntries は未移植**（W19b）。W19a の配線は `pronunciations` をそのまま `SyncAsync` に渡す。`ArtistProfile.Reading` は未追加。
