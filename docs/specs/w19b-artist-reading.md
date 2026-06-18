# W19b — 読み正確化②：アーティスト読みの自動登録

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。挙動の正は Mac 版 shipped コード（s19b）。
> W19a（`w19a-pronunciation-dictionary.md`）の続き。W19a の辞書同期機構（`VoicevoxUserDict`）に、アーティスト名の読みを合流させる。
> 参照: `w19a-pronunciation-dictionary.md` §12（申し送り）, `w15-artist-feature.md`（`reading` を後方互換の将来拡張として記載）。

## 1. 概要

「アーティスト一覧を生成」時に各アーティストのカタカナ読みも LLM に併出させ `config/artists.yaml` に保存し、W19a の辞書同期に合流させる（アーティスト名が DJ の発話テキストに現れたら正しい読みで発音）。

- 例: `Mr.Children → ミスターチルドレン`、`Official髭男dism → オフィシャルヒゲダンディズム`、`米津玄師 → ヨネヅケンシ`。
- 効き方は W19a と同じ VOICEVOX ユーザー辞書経由。空白を含む純英字バンド名（`SEKAI NO OWARI` / `BUMP OF CHICKEN`）は効かない（W19a §1.1 の制約。形態素解析で照合されない）→ 通称カナ運用で割り切り（ユーザー判断 2026-06-14）。
- 破壊的変更: 既存「アーティスト一覧を生成」ボタンの LLM 出力契約（名前のみ → 名前＋読み）を変える。独立スライス・独立ライブ確認。

## 2. データモデルの変更（Core）

`ArtistProfile`（`AIRadio.Core/ArtistFeature.cs`）に任意フィールド `Reading` を追加（後方互換）。

```csharp
public sealed record ArtistProfile(string Id, string Name, string? Reading = null);
```

- 既定 `null`。既存の `new ArtistProfile(id, name)` 呼び出し（`ArtistListGenerator` / `ArtistsConfig` / 各テスト）は無改修。record の値等価は `Reading=null` 同士で従来どおり一致（`new ArtistProfile("a","b") == new ArtistProfile("a","b", null)`）。

## 3. 生成・保存・読込の変更（Infra）

### 3.1 `ArtistsConfig`（読込）
`ArtistDto` に `string? Reading` を追加 → `new ArtistProfile(a.Id, a.Name, a.Reading)`。任意なので欠落許容（既存 `artists.yaml` はそのまま読める）。W15 の「reading は読まない（`IgnoreUnmatchedProperties` で無視）」を解除する。

### 3.2 `ArtistListGenerator`（生成）
- プロンプト（`MakeRequest`）: 出力を `アーティスト名<TAB>カタカナ読み` の 2 列に変更。読み不明なら名前のみ可。例を 1 行示す（`例: 米津玄師<TAB>ヨネヅケンシ`）。読みは全角カタカナで、と明示。
- パース（`ParseEntries`）: 戻り値を `IReadOnlyList<string>` から `IReadOnlyList<(string Name, string? Reading)>` に変更。1 行を最初のタブで 2 分割し `(Name, Reading?)` を返す（§3.3 の逸脱耐性）。
- 検証・採用（`GenerateAsync`）: 従来どおり `Name` で Spotify 実在検証（`TopTracksAsync`）・`CanonicalName` 去重。採用時に `Reading` を保持し `new ArtistProfile(id, name, reading)` を作る。`MakeRequest` の excluding には `entries.Select(e => e.Name)` を渡す。
- 書き出し（`Write`）: `OutArtist` に `string? Reading` を追加。null のときキーを出力しない（`SerializerBuilder().ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)` で `reading: null` を出さない＝artists.yaml を汚さない）。ヘッダコメントに `reading` は任意のカタカナ読みと明記。原子的上書き idiom（`File.Replace`／初回 `File.Move`・BOM なし）は不変。

### 3.3 `ParseEntries` の逸脱耐性（仕様・必須。LLM は契約を破る前提で設計）
`raw.Split('\n')` した各行について（規則の集合であり厳密な逐次手順ではない）:
1. **列分割**: 最初のタブで分割（`rawLine.Split('\t')`）。1 列目＝名前候補、2 列目＝読み候補、3 列目以降は無視。
2. **名前列の前処理**: `columns[0]` を `.Trim()`（C# 既定＝行頭行末の全空白＝`\r`/`\n`/タブ/半角全角空白を除去）→ 行頭の bullet/番号を除去 → 引用符・空白を除去（W15 と同一 idiom）。**この `.Trim()` で名前列の `\r` は確実に除去される**（タブ無し CRLF 行の末尾 `\r` 含む）。
3. **タブ無し行は救済**: 2 列目が無ければ `(Name, null)`＝名前のみ（既存の名前生成を退行させない。読みが付かないだけ）。
4. **読み列の正規化＋検証**: `columns[1]` を**引用符・空白・`\r` を除去**（`raw.Split('\n')` で CRLF 入力の末尾 `\r` が最終列＝読み列に残るため。Mac は trim 集合に `\r` を含めないが、Gemini は `\n` 区切りゆえ実害なし＝Win の CRLF 堅牢化）→ `VoicevoxUserDict.NormalizePronunciation`（NFKC・半角カナ→全角カナ）→ `VoicevoxUserDict.IsKatakana`。カタカナ以外（ひらがな・漢字・英字・「不明」等・空）を含むなら `Reading = null` にして捨てる（VOICEVOX に投げて 422 を取りに行かない）。
5. **`Name` が空の行はスキップ**。

## 4. 辞書への反映（Infra・W19a と結線）

`VoicevoxUserDict.MergedEntries(pronunciations, artists)`（W19a §12 / W-6 で計画済み）を実体化:

```csharp
public static IReadOnlyList<PronunciationEntry> MergedEntries(
    IReadOnlyList<PronunciationEntry> pronunciations, IReadOnlyList<ArtistProfile> artists);
```

- `pronunciations` を先頭（明示指定が最優先）。`artists` のうち `Reading` が非 null・非空のものを `new PronunciationEntry(name, reading, AccentType: 0, WordType: "PROPER_NOUN")` として追加。
- surface の重複は `MatchKey`（NFKC）先勝ち＝pronunciations.yaml が勝つ。`seen` は `pronunciations` の MatchKey で初期化。
- `MatchKey`（NFKC）は `VoicevoxUserDict` の private static だが、`MergedEntries` は同クラスの static メソッドゆえアクセス可（公開不要）。
- 配線: `BroadcastComposition.RunAsync` の `SyncAsync(pronunciations, ct)` を `SyncAsync(VoicevoxUserDict.MergedEntries(pronunciations, artists), ct)` に差し替える（`artists` は W15 で既にロード済み）。同期タイミング・冪等・fail-tolerant は W19a のまま。

## 5. 後方互換・運用

- 既存 artists.yaml（読みなし）はそのまま動く（`Reading=null` → 辞書に登録しないだけ）。
- 既に生成済みの組は旧生成（読みなし）なので、読みを付けるには「アーティスト一覧を生成」を再実行する（次の放送開始で読みが辞書へ）。
- 生成直後ではなく次の放送開始で反映（W19a と同じタイミング。生成直後同期は将来余地）。
- 空白英字バンド名は効かない（W19a §1.1）。生成で読みは付くが辞書照合されない。割り切り（通称カナは手編集 pronunciations.yaml 側で対応可）。

## 6. エラー / fail-tolerant

- 読み生成は LLM 任せ＝不確実。reading は完全に任意（無くても名前のみで動く＝放送に無影響）。新エラーコードは増やさない。
- 非カタカナ読みは `ParseEntries` で捨てる（VOICEVOX へ送る前）。辞書側の個別失敗は W19a の `E-PRON-WORD-REJECTED-001`（fail-tolerant）に集約。
- 生成全体の失敗（実在検証で全滅等）は従来の `ArtistGenException`（生成ツール固有・放送系外）。

## 7. Win 固有 parity 注記（確定）

- W-1. `ArtistProfile.Reading` は record の 3 番目の任意 positional パラメータ（`= null`）。既存 2 引数生成は無改修・record 値等価は維持。
- W-2. `ParseEntries` 戻り値は `IReadOnlyList<(string Name, string? Reading)>`（Mac の `[(name, reading?)]` に対応・値タプル）。読み候補は引用符/空白に加え `\r` も除去（CRLF 堅牢化。Mac の trim 集合は `\r` を含まないが、Gemini は `\n` 区切りのため実害なし＝Win 改善）。読みの正規化・検証は `VoicevoxUserDict.NormalizePronunciation`/`IsKatakana` を `private`→`internal` に開放して共用（同一 assembly 内。新規 public API を増やさない＝§3-5）。
- W-3. `Write` は YamlDotNet `DefaultValuesHandling.OmitNull` で `reading=null` を出力しない（Mac の手書き `encode(to:)` 相当）。`Id`/`Name` は非 null ゆえ常に出力。原子的上書き idiom は不変。
- W-4. `MergedEntries` は `VoicevoxUserDict` の public static（Mac の extension 相当）。`MatchKey`（private static）に同クラス内からアクセス。pronunciations 先頭・最優先、artist reading は PROPER_NOUN、NFKC 先勝ち。
- W-5. 配線は `BroadcastComposition.RunAsync` の sync 呼び出し 1 行を merged に差し替えるのみ。`PronunciationsConfig.LoadFile`/`ArtistsConfig.LoadFile` の両ロードは既存。
- W-6. `ParseEntries`/`MakeRequest` は `public static`（W15 から。直接単体テスト用）。戻り値型変更は内部消費者（`GenerateAsync`）のみで、既存テストは `GenerateAsync` 経由ゆえ非破壊（名前のみ LLM 応答は `Reading=null` でパース）。

## 8. テスト（`dotnet test AIRadio.slnx` グリーンが完了条件）

- `ArtistListGenerator.ParseEntries`（逸脱耐性が核）:
  - `"米津玄師\tヨネヅケンシ"` → `("米津玄師", "ヨネヅケンシ")`。
  - `"あいみょん"`（タブ無し）→ `("あいみょん", null)`（救済）。
  - bullet/番号付き `"- 1. サザンオールスターズ\tサザンオールスターズ"` → 名前・読みとも除去後に取得。
  - 半角カナ読み `"A\tｱｲﾐｮﾝ"` → `("A", "アイミョン")`（正規化）。
  - 非カタカナ読み（ひらがな `"X\tえっくす"` / 英字 `"Y\tYomi"` / `"Z\t不明"`）→ `Reading=null`。
  - タブ複数 `"name\tヨミ\textra"` → `("name", "ヨミ")`（2 列目のみ）。
  - 空名 `"\tヨミ"` → スキップ。
  - CRLF（2 列）`"name\tヨミ\r"`（`\n` 分割後の末尾 `\r`）→ `("name", "ヨミ")`（読み列の `\r` 除去）。
  - CRLF（タブ無し）`"あいみょん\r"` → `("あいみょん", null)`（名前列の `.Trim()` が `\r` を除去）。
- `ArtistsConfig`: `reading` ありで `ArtistProfile.Reading` がセット／無しで null（後方互換）。
- `ArtistListGenerator.Write`: **生成 YAML テキストを直接検査**（`File.ReadAllText`）。reading 付き組は本文に `reading: ヨネヅケンシ` を含む／reading=null のみの組は本文に `reading:` を含まない（`DefaultValuesHandling.OmitNull` が効いていることを固定。往復だけでは出力有無を検証できないため）。`LoadFile` 往復は Name 健全性・非 null reading の保持の併用チェックとして残す。
- `ArtistListGenerator.GenerateAsync`（fake LLM/catalog）: 2 列出力をパースし reading 付き `ArtistProfile` を書く／タブ無し応答でも名前のみで生成継続（退行なし）。
- `VoicevoxUserDict.MergedEntries`:
  - pronunciations 優先で artist reading を統合／artist 由来エントリは `WordType=PROPER_NOUN`・`AccentType=0`。
  - **surface 重複は pronunciations 勝ち（NFKC 跨ぎ）**: pronunciations=`[("Mr.Children","ミスチル")]`、artists=`[("a1","Ｍｒ．Ｃｈｉｌｄｒｅｎ"（全角）, "ミスターチルドレン")]` → `Count==1`・唯一要素の `Pronunciation=="ミスチル"`（明示指定が勝つ）・`Surface=="Mr.Children"`（pronunciations の raw）。素の文字列 == で dedup すると `Count==2` で落ちる＝`MatchKey`（NFKC）採用を担保。
  - **`Reading=null` も `Reading=""`（空文字）も対象外**（`PROPER_NOUN` エントリを作らない＝§4「非 null・非空」。`[Theory]` で null/"" をまとめて検証）。手編集 `artists.yaml` の `reading: ""` は `ArtistProfile.Reading=""` になり得る（ローダは空→null 正規化しない）ため到達可能。
- `MakeRequest`: プロンプトにタブ区切り出力形式・読み例・全角カタカナ指定が含まれる。

## 9. 受け入れ基準（ライブ確認・最後に一括）

1. 「アーティスト一覧を生成」→ `config/artists.yaml` に `reading:` が併記される（日本語名・記号交じり英字名で）。
2. 生成後に放送開始 → 冒頭「📖 読み辞書同期」ログにアーティスト読みぶんの追加が出る。
3. アーティスト特集や通常トークでアーティスト名が正しい読みで発音される（例「Mr.Children＝ミスターチルドレン」）。
4. 空白純英字バンド名は効かない＝仕様（W19a §1.1）。
5. 既存（読みなし）artists.yaml でも従来どおり放送が回る（後方互換）。

## 10. スコープ外（W19b）

- 生成直後の即時辞書同期（次の放送開始で反映で足りる）。
- 空白英字名の根本対応（合成前テキスト置換層。W19a §12 の将来検討）。
- アクセント（`accent_type`）の自動推定（既定 0）。読みの手動上書き UI（手編集 artists.yaml / pronunciations.yaml で代替）。

## 11. 設計判断（確定 — Mac s19b §10 を踏襲）

- A. 出力契約: タブ区切り `名前<TAB>読み`。タブ無しは名前のみで救済（退行なし）。
- B. 読み検証: 非カタカナ読みは捨てる（null 化）。VOICEVOX に投げない。
- C. 反映タイミング: 次の放送開始（生成直後同期はしない）。W19a と揃える。
- D. 空白英字名: 割り切り（W19a 踏襲）。
