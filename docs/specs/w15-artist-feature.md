# W15 — アーティスト特集（1 放送 1 回、ゲストコーナーの直後に固定）

> 仕様駆動ワークフロー（CLAUDE.md §2-2 / Mac §7）の SoT。**実装前にユーザーレビューを受ける。**
> 本仕様は Mac `docs/specs/s15-artist-feature.md` の **Windows 版（C#/.NET 10 + Avalonia 12）移植** である。
> Mac の設計判断はユーザー確定済み（2026-06-13）。挙動は Mac を「正」とし忠実移植する。
> Win 固有の適応・確定判断は §18 に集約する（移植時に最優先で参照）。
> 参照: `w14-guest-corner.md`（直近の前例）、`error-codes.md`、`../../CLAUDE.md`、Mac `s15-artist-feature.md`。
> ※ file:line 表記は 2026-06-17 時点（W14 commit `f086fc8` / HEAD）のコード地図に基づく指標。実装時に現物で再確認する。

---

## 1. 概要

1 放送に 1 回だけ、**ゲストコーナーの直後**に「アーティスト特集」を挿入する。`config/artists.yaml`（新規）の
アーティスト一覧から**実行時にランダムで 1 組**を選び、その日のメイン／サブ DJ（W13.5）が、そのアーティストの
**最大 7 曲**を「導入 → 3 曲紹介 → 3 曲連続 → 感想 → 3 曲紹介 → 3 曲連続 → 感想（短め）→ 1 曲紹介 → 1 曲 → 締め」の
固定フローでお届けする特別ブロック。番組は **最初のニュース → ゲストコーナー → アーティスト特集** という特別ブロックを
持つ（ゲストも特集も 1 放送 1 回）。

ゲストコーナー（W14）が「1 コーナー＝1 talk セグメント＝1 曲」だったのに対し、アーティスト特集は
**1 ブロックの中に複数の発話パートと最大 7 曲が交互に並ぶ**点が構造的に新しい。よって W14 の `guest`（`SegmentKind.Talk` で表現）
のように既存 `CornerEngine`／`PreparedCorner`（単曲モデル）へは乗らず、**マルチ曲対応の専用準備物・専用ランナー・専用
`SegmentKind`** を新設する（§10）。配置の決定論性・fail-fast・乱数注入・完全静寂・config ローダ同形は W14 を踏襲する。

**曲の取得**は LLM に曲名を挙げさせるのではなく、**Spotify の「アーティスト上位曲（top-tracks）」API**で確定する（§5）。
これにより実在の正確な曲名・URI が得られ、「紹介した曲が流れない」事故ゼロ（CLAUDE.md §3-2）と紹介台本の曲名一致を両立する。

`config/artists.yaml` は**出荷時は空**で、**トレイメニューの「アーティスト一覧を生成」項目**（放送停止時のみ有効）で LLM に
アーティストを選ばせて**上書き**する（§9）。**ジャンルと件数は `config/artist-gen.yaml`（テキスト編集）で指定**（既定: 邦楽中心・
100 名。洋楽・クラシック等に変更可、ハードコードしない）。一度も生成しない限り特集は出ない（プールが空なら特集をスキップ）。

---

## 2. 用語

- **アーティスト特集（artist feature）**: 本仕様で追加する特別ブロック。1 組のアーティストの最大 7 曲を構成して流す。
- **特集アーティスト（featured artist）**: `artists.yaml` から実行時にランダム選定される 1 組（`ArtistProfile`）。
- **7 曲＝3+3+1**: 前半グループ 3 曲・中盤グループ 3 曲・ラストグループ 1 曲の最大 3 グループ。
- **発話パート（feature part）**: ブロック内の各発話単位（導入 / グループ紹介 / 感想 / 締め）。曲はパートではない。
- **プレフライト**: 流す曲の再生可否確認（CLAUDE.md §3-2）。本仕様では top-tracks 取得後に確定し、台本生成より前に完了する。
- **縮約**: 再生可能曲が 7 曲に満たないときの構成短縮（§6）。
- **K**: 重複除外・採用後に確定した、実際に流せる曲数（0〜7）。

---

## 3. 配置（`ProgramPlan` が生成、ゲスト直後・固定位置・1 回）

W14 の「最初の news 直後にゲスト talk を 1 つ挿入」のすぐ後ろに、特集セグメントを 1 つ挿入する。
W14 と同じく**位置は決定論的に固定**（ランダム枠選択はしない）。Mac s15 §3 に忠実。

### 3-1. 新しい `SegmentKind`

- `SegmentKind`（[Program.cs:9-25](../../AIRadio.Core/Program.cs)、現在 `Opening, Song, Talk, News, Ending` の 5 値）に
  **`ArtistFeature`** を追加する。ゲストは `Talk`（CornerId=guest）で表現したが、特集は**曲の本数・進行が talk と別物**なので
  独立値とする（この非対称は意図的。レビュー誤認防止のため明記）。
- **追加位置 = `Ending` の後ろ（末尾）**。**これは enum の宣言順（各値の序数）の話であり、放送順とは無関係**（特集の放送配置は §3-3 の body 5 固定＝ゲスト直後・ED の手前。`Ending` は従来どおり放送の最後で、放送が ED で終わる挙動は変わらない）。Mac の宣言順は `…News, ArtistFeature, Ending` だが、Win では `Ending` の前に割り込むと
  `Ending` の序数がずれる。**序数依存箇所が将来生まれたときの事故を避けるため末尾追加とし、Mac↔Win の序数 parity は意図的に取らない**
  （§18-1）。`:7` の「`ArtistFeature`（W15）は後続で追加する」コメントを更新する。
- **網羅監査（必須・C# は強制されない）**: Swift の `switch` は `default` 無しなら case 追加でコンパイルエラーになるが、**C# の
  `switch` 文は網羅を強制しない**。`SegmentKind` を分岐する全箇所に明示 case を手で追加する。最低限 `BroadcastEngine` の
  `StartPreparation`（[BroadcastEngine.cs:271-324](../../AIRadio.Core/BroadcastEngine.cs)、現状 `default` 無し）と
  `PerformAsync`（[:417-492](../../AIRadio.Core/BroadcastEngine.cs)、同）の 2 つ。落とすと「plan は特集を生成するが engine が演奏しない」
  沈黙バグになる（§18-2）。
  - **さらに switch ではないが Kind を否定形で分岐する箇所が 1 つ**: ED 早終了ガード（[:235](../../AIRadio.Core/BroadcastEngine.cs)、`segment.Kind != SegmentKind.Ending`）に
    `&& segment.Kind != SegmentKind.ArtistFeature` を追加（§8-5・§10-2。落とすと特集直後に余分なトークが 1 本挟まる可聴バグ＝§8-5 が防ぐ事象。これは `case` 追加でなく `if` 条件への連言追加。Mac BroadcastEngine.swift:229 が同編集）。

### 3-2. blueprint と挿入条件

- **`ProgramBlueprint.ArtistFeatureCornerId : string?`** を追加（[Program.cs:121-134](../../AIRadio.Core/Program.cs)。`GuestCornerId`
  の直後に**末尾 optional positional param** として追加＝既存生成箇所・`with{}` 非破壊）。`program.yaml` の `artist_feature.corner_id`、
  null で無効。
- **`IncludesArtistFeature`** = `ArtistFeatureCornerId is not null && IncludesGuestCorner`。すなわち**特集はゲストコーナーに従属**する
  （ゲストが入る放送＝特集も入る／ゲストが入らない放送＝特集も入らない）。`IncludesGuestCorner` は「最初の news があるとき」＝
  **N ≥ 2（有限）/ エンドレス**（W14）。
  - 実運用のトレイ「番組の長さ」は 10 / 20 / 30（W13 でエンドレスは UI 非露出に確定）なので**実質必ず登場**する。N=2〜3 の極小番組では
    特集が番組の大半を占めるが、確定フロー「ニュース→ゲスト→特集」の帰結として**許容**（抑止閾値は設けない）。
- **実行時の従属（プール）**: 上記は plan 生成（config レベル）の条件。さらに**実行時に artists.yaml のプールが空（未生成）なら特集は
  スキップ**される（§8-4）。plan は特集を必ず 1 個置き、engine が「プール空」または「K ≤ 2」で握りつぶす。

### 3-3. 挿入ロジック（`InsertionsBefore` ヘルパーに一本化）

W14 のゲスト挿入は「guest を body 4 に割り込ませ、以降の素パターン参照を `body-1` でずらす」だった（現
[Program.cs:176](../../AIRadio.Core/Program.cs) `InsertionsBefore` は `GuestBodyPosition is int gp && gp < body ? 1 : 0`）。特集を body 5
に足すと割り込みが 2 つになり手計算分岐が壊れやすい。**割り込みを数えて差し引く単一ロジックに一般化**する（Mac s15 §3-3）。

- 位置: `GuestBodyPosition => IncludesGuestCorner ? 4 : null`（既存）、`ArtistFeatureBodyPosition => IncludesArtistFeature ? 5 : null`（新規・private）。
- `InsertionsBefore(int body)` を**両位置を数える形に書き換え**:
  ```csharp
  private int InsertionsBefore(int body)
  {
      var c = 0;
      if (GuestBodyPosition is int g && g < body) c++;
      if (ArtistFeatureBodyPosition is int a && a < body) c++;
      return c;
  }
  ```
  （LINQ でなく明示カウンタ＝セグメント毎呼出のホットパスでアロケーション回避。Mac の `compactMap{}.filter{}.count` と等価。）
- `BodySegment(int body)`（[:217-224](../../AIRadio.Core/Program.cs)）:
  - `body == GuestBodyPosition` → ゲスト talk（`new ProgramSegment(SegmentKind.Talk, Blueprint.GuestCornerId)`、W14 のまま）
  - `body == ArtistFeatureBodyPosition` → `new ProgramSegment(SegmentKind.ArtistFeature, Blueprint.ArtistFeatureCornerId, Critical: false)`
    （`Critical: false` を**明示**＝「常に非 critical」契約を grep 可視化、§11）
  - それ以外 → `PatternBodySegment(body - InsertionsBefore(body))`
- **端数トーク・ED 判定は「割り込みを除いた素 body 空間」で行う**（`PatternBodySegment` は正規化後の body に対して動く）。
  これでゲストのみ有効（＝従来 `body-1`）も自動再現し、**W14 既存テスト（[ProgramPlanTests.cs:132-187](../../AIRadio.Core.Tests/ProgramPlanTests.cs)）を壊さない**
  （この回帰グリーンが `InsertionsBefore` 一般化の安全網）。
- **`TotalSegmentCount`**（[:182-193](../../AIRadio.Core/Program.cs)）: 有限番組では `+ (IncludesArtistFeature ? 1 : 0)`（guest と合わせ +2）。
  **エンドレスは従来どおり null**。`「（+ ゲスト 1）」` のコメントを `「（+ ゲスト 1 + 特集 1）」` に更新。
- **数えない**: 特集は N（トーク数）に数えない（letter / news / guest と同じ）。
- **エンドレス**: 特集は body 5 固定で本編ループの繰り返し対象に入らず、エンドレスでも 1 回だけ出る。
- **スキップと `TotalSegmentCount` の関係**: plan は必ず特集を 1 個置くので、実行時にスキップ（§8-4）されても `TotalSegmentCount` は 1
  多いまま。これは §8-4・Mac §3-2 で**許容**（plan は実行時の K を知らない。`TotalSegmentCount` を後付け減算してはならない）。

### 3-4. セグメント列の例（`ProgramPlanArtistFeatureTests.swift` で検証済み）

- N=2: `OP → song → talk → talk → letter → news → guest → 特集 → ED`（total 9）
- N=3（奇数端数 × 2 挿入）: `OP → song → talk → talk → letter → news → guest → 特集 → talk → ED`（total 10。ED の絶対 index が +2 ずれ、奇数端数 talk は素 body 空間で判定）
- N=4: `OP → song → t → t → letter → news → guest → 特集 → t → t → letter → news → ED`（特集 1 回・2 回目 news 後は無し。total 13）
- N=5: `OP → song → t → t → letter → news → guest → 特集 → t → t → letter → news → t → ED`
- エンドレス: prefix が `… news → guest → 特集` で特集ちょうど 1 個・`TotalSegmentCount` は null
- ゲスト無効 → 特集も無し（`IncludesArtistFeature == false`）
- N=1: news が無く guest/特集とも無し（`opening, song, talk, ending`・total 4）

### 3-5. アーティスト選定（人選のみ乱数）

放送開始時に `BroadcastEngine` が `artists.yaml` から `_randomIndex(count)` で 1 組選ぶ（W14 のゲスト選定と同じ
**既存 `Func<int,int>?` 注入**＝[BroadcastEngine.cs:64,92](../../AIRadio.Core/BroadcastEngine.cs)。新規 RNG は作らない）。テストは固定値で決定論。
選定アーティストは特集セグメントの準備に渡す（§10）。**プールが空なら選定せず `null`（スキップ）**＝ゲストと異なり fail-fast しない（§18-3）。

---

## 4. アーティスト特集ブロックの構造（`corners.yaml` に `artist_feature` コーナー追加、format: artist_feature）

頭出しリード文（時報、その日のメインが読む）→ 本編（紹介・演奏・感想の交互）→ 締め、の二層構成は他コーナーと同じ。
ただし**本編が「複数の発話パート × 最大 7 曲」**である点が新しい。`CornerFormat.ArtistFeature` は **W14 で先行追加済み**
（[Dialogue.cs:29](../../AIRadio.Core/Dialogue.cs)、`CornersConfig.ParseFormat` も `"artist_feature"` をマップ済み[:70](../../AIRadio.Infrastructure/Config/CornersConfig.cs)）。

- **リード文（時報つき、その日のメインが読む）**:
  `"{ampm}{hour}時{minute}分になりました。ここからはアーティスト特集です。本日は{artist}さんを特集します。"`
  - 時刻プレースホルダの公式語彙は corners.yaml 規約（`{ampm}`/`{hour}`/`{hour12}`/`{minute}`）。**時刻は発話直前に展開**（再生時点で正確）。
  - **`{artist}` は準備時に置換**（`ArtistFeatureEngine.PrepareAsync` 内で `featuredArtist.Name` を `TemplateExpander`/`TimePhrases` の同じ
    呼び方で埋める。`CornerEngine` の `{guest}`/`{theme}` 置換ロジックは format 分岐に紐づくので流用しない）。リード文に `{theme}` は使わない。
- **フロー（K=7 のフル形。話者: メイン＝当日先頭、サブ＝以降、全員＝メイン＋サブ）**:

  | # | 種別 | 内容 | 話者 | 直後の曲 |
  |---|---|---|---|---|
  | (1) | 導入（発話） | 短い特集宣言＋そのアーティストへの思いを一言 | メイン主導（サブ相づち） | — |
  | (2) | 前半グループ紹介（発話） | 1〜3 曲目をまとめて紹介（曲名・聴きどころ） | メイン主導、サブ補足 | — |
  | (3) | 前半グループ（演奏） | 3 曲を**連続再生**（曲間に発話なし） | — | 1・2・3 曲目 |
  | (4) | 感想・会話（発話） | 前半 3 曲の感想・雑談 | 全員 | — |
  | (5) | 中盤グループ紹介（発話） | 4〜6 曲目をまとめて紹介 | メイン主導、サブ補足 | — |
  | (6) | 中盤グループ（演奏） | 3 曲を**連続再生** | — | 4・5・6 曲目 |
  | (7) | 感想（発話） | 中盤 3 曲の感想（**(4) より短い**） | 全員 | — |
  | (8) | ラストグループ紹介（発話） | 7 曲目を紹介 | メイン主導 | — |
  | (9) | ラストグループ（演奏） | 7 曲目を再生 | — | 7 曲目 |
  | (10) | 締め（発話） | 「以上、{artist}特集でした。」で締める（固定行・準備時に実名展開） | メイン | — |

  - **感想は「最後のグループを除く各グループの後」に入る**。K=7 では感想は (4)(7) の 2 回（(4) 長め・(7) 短め）。**締めはグループ感想の
    代わりに最終グループ後へ 1 回**。縮約時の感想回数は §6。
- 各曲の音量・フル再生（`play_seconds`）・連続再生・完全静寂は §8。

---

## 5. 曲の取得とプレフライト（Spotify top-tracks 方式）

LLM に曲名を挙げさせる方式は採らない（`SongPicker` の候補上限で 7 曲に届かず、実在曲名の保証も弱い）。代わりに
**Spotify の「アーティスト上位曲」**で実曲を確定する。

- **新規 interface `IArtistCatalog`（Core）**: `Task<IReadOnlyList<TrackInfo>> TopTracksAsync(string artistName, int limit, CancellationToken ct = default);`
  （[Abstractions.cs](../../AIRadio.Core/Abstractions.cs) に `ITrackSearcher` 隣に追加）。**真のシーム**（Spotify 実装＋テスト fake）なので
  `I` プレフィックス interface 化（CLAUDE.md §3-5。`ITrackSearcher`/`ISpotifyController` と同列）。§18-4。
- 実装 `SpotifyArtistCatalog : IArtistCatalog`（Infra 新規、[SpotifyWebSearcher.cs](../../AIRadio.Infrastructure/Spotify/SpotifyWebSearcher.cs) と同形）:
  ctor `(ISpotifyTokenProvider auth, IHttpClient http, string market = "JP")`。
  1. `GET /v1/search?q=<EscapeDataString(name)>&type=artist&limit=1&market=<market>` でアーティスト ID 解決（先頭ヒット）。
     **未解決は throw せず空リストを返す**（Mac 準拠。K<3 スキップへ流す）。
  2. `GET /v1/artists/{id}/top-tracks?market=<market>` で上位曲を取得。
  3. `TrackInfo(Uri, Title=name, Artist=artists[0].name ?? artistName, IsPlayable=is_playable ?? true)` へ変換し `.Take(limit)`。
  - URL は `Uri.EscapeDataString` 補間（Win 流儀）、JSON は `System.Text.Json` + `[JsonPropertyName]`（`SpotifyWebSearcher` 同様、global
    snake_case ポリシーは使わない）。エラー三段: `SpotifyException` 再throw / `OperationCanceledException` 再throw / その他は
    `SpotifyException.SearchFailed(ex.Message)`（既存 `E-SPT-SEARCH-FAILED-001`）。market は `SpotifyConfig.Market`（既定 JP）を流用、新キーなし。
- **`TrackInfo` は変更しない**（duration フィールドは追加しない。Mac/Win とも持たず、再生長は `play_seconds`／終端検知で決まる。§18-5）。
- **曲の確定はランナー側（`ArtistFeatureEngine.PrepareAsync`）で、台本生成より前に**（CLAUDE.md §3-2）。catalog は thin（resolve→fetch→Take(limit)）に保つ:
  1. `TopTracksAsync(artistName, limit: FetchLimit=10, ct)` を取得。
  2. **重複除外**（`Deduplicate`/`CanonicalTitle`）: 同一 URI、および正規化タイトル（小文字化＋括弧内バージョン表記 `Remaster`/`Live`/`feat.` 等
     を正規表現 `[\(（\[【].*?[\)）\]】]` で除去＋空白除去）が一致する曲を 1 つに集約。
  3. 頭から**最大 `TargetTracks=7` 曲**を採用。確定実曲数を **K**（0〜7）とする。
  4. **`IsPlayable` 二重確認はしない**（Mac の shipped engine も呼ばない。top-tracks は market フィルタ済み＝`IsPlayable ?? true` を信頼。§18-6）。
- **フォールバック曲で水増ししない**（名指しブロックゆえ）。K に応じて §6 の縮約に従う。`K < MinTracks=3` は特集スキップ（§6・§8-4）。
- `FetchLimit=10` / `TargetTracks=7` / `MinTracks=3` は **`ArtistFeatureEngine` の定数**（catalog はハードコードしない）。

---

## 6. 縮約ルール（再生可能曲が 7 曲に満たないとき）

確定実曲数 K でグループ構成と感想回数を**決定論的に**決める。縮約は**プレフライト完了後・台本生成前**に確定（紹介曲名と台本が必ず一致）。
**最低曲数は 3**（導入 + 1 グループ + 締めが成立する下限）。

### 6-1. 分割アルゴリズム（`SplitGroups`）

```
front = min(3, K); rem = K - front; mid = min(3, rem); last = rem - mid;
groups = [front, mid, last] のうち 0 を除いたもの
// K=7→[3,3,1], 6→[3,3], 5→[3,2], 4→[3,1], 3→[3]
// フロー: 導入 → 各グループ(紹介→連続再生→(最後でなければ)感想) → 締め
// 感想は「最後のグループを除く各グループ後」。感想[0] 長め、感想[1..] 短め。締めは最終グループ後 1 回。
```

### 6-2. 縮約表

| K | groups | G | 感想回数 | 感想 target | 流れ |
|---|---|---|---|---|---|
| 7 | 3+3+1 | 3 | 2 | (4)=comment / (7)=comment_short | フル（§4 (1)〜(10)） |
| 6 | 3+3 | 2 | 1 | comment | 導入 → 3 紹介 → 3 → 感想 → 3 紹介 → 3 → 締め |
| 5 | 3+2 | 2 | 1 | comment | 導入 → 3 紹介 → 3 → 感想 → 2 紹介 → 2 → 締め |
| 4 | 3+1 | 2 | 1 | comment | 導入 → 3 紹介 → 3 → 感想 → 1 紹介 → 1 → 締め |
| 3 | 3 | 1 | 0 | — | 導入 → 3 紹介 → 3 → 締め |
| 0〜2 | — | — | — | — | **特集スキップ**（§8-4） |

- 2 回目感想 `comment_short` は K=7 でのみ使用。各グループ紹介は**そのグループの実曲名・アーティスト名**を必ず正確に含む（§7）。

---

## 7. 台本生成（`DialogueScriptGenerator.MakeArtistFeatureRequest` 新設）

- **ペルソナ＝その日の DJ**: その日のメイン／サブ（W13.5）。`CastDjIds`（先頭＝メイン）を使う。ゲスト（W14）は登場しない。
- **`MakeRequest` は変更しない**。W14 は `MakeRequest` に `guest` 引数を足したが、特集は**兄弟ビルダー**として新設する（既存 free_talk/letter/guest/greeting 分岐は不変＝回帰保護）:
  ```csharp
  public static LLMRequest MakeArtistFeatureRequest(
      ArtistFeaturePart part, string artistName, IReadOnlyList<DjProfile> djs,
      string dateContext = "", int targetCharacters = 0, double temperature = 0.9)
  ```
  - `names`/`main`/`profiles` は `MakeRequest`（[:41-43](../../AIRadio.Core/DialogueScriptGenerator.cs)）と同一に再構成。
  - 共有ベース制約（`MakeRequest` の `constraints` [:66-73](../../AIRadio.Core/DialogueScriptGenerator.cs) と同一文言）＋**途中コーナー句**を積む。
    途中句は **Win の長い anti-show-close 形**（`MakeRequest` else 分岐 [:81](../../AIRadio.Core/DialogueScriptGenerator.cs) と同一＝
    「番組全体を締めくくる言い方（…ラストナンバー/また来週/お別れの時間 等）はしない」を含む W12 由来形）を使う（§18-7。Mac は短縮形だが Win は一貫性優先）。
- **`ArtistFeaturePart`**（マルチ曲は `GroupIntro` のペイロードに載る。`song:` 引数とは別経路）:
  ```csharp
  public abstract record ArtistFeaturePart
  {
      public sealed record Intro : ArtistFeaturePart;
      public sealed record GroupIntro(IReadOnlyList<TrackInfo> Tracks, int Index, int Total) : ArtistFeaturePart;
      public sealed record Comment(bool Shorter) : ArtistFeaturePart;
  }
  ```
  （Swift enum-with-associated-values → Win の `abstract record` + nested `sealed record` 慣用＝`CornerEvent`/`BroadcastEvent` と同型。enum にはしない。）
- **パート別分岐**（`switch (part)`、未知 case は `throw` で網羅明示）:
  - **(1) `Intro`**: 見出し「アーティスト特集の『導入』」。特集宣言＋`artistName` への思いを一言・短め。**「まだ曲名には触れない」**（曲名は次パート）。target=`intro_target_chars`。
  - **グループ紹介 `GroupIntro` (2)(5)(8)**: 「# 紹介する曲（この順で）」に当該グループの `string.Join("、", tracks.Select(t => $"「{t.Title}」（{t.Artist}）"))` を列挙。
    **「曲名・アーティスト名は与えられた表記を一字一句そのまま言い、言い換え・推測・別情報の捏造をしない。」**制約を明記。target=`group_intro_target_chars`。
    位置依存（会話の連続感）:
    - **1 回目（`Index==0`。K=3 の唯一グループ `Total==1` 含む）**: 素直に「各曲に聴きどころを軽く添え、曲へ送り出して締める」。
    - **2 回目以降（`Index>0`）**: いま進行中の同じ特集を**「続ける」つなぎ**（「引き続き〈artist〉の曲を」「同じ〈artist〉から、次は」）で前を受け、
      アーティスト紹介をやり直さない。**「続いては〈artist〉特集」のように新しく特集が始まる言い方は禁止**（ライブ確認 fix2 相当）。
    - **最後のグループ（`Index == Total-1 && Index>0`）**: 「最後はこの N 曲」のようにラストである旨を一言（縮約で 1〜3 曲。文字列 `$"最後はこの {n} 曲"` の前後空白に注意）。
  - **感想 `Comment` (4)(7)**: 見出し「今流した曲を聴いたあとの感想・雑談」。`Shorter` なら「前の感想より短く、テンポよく」、else「自由に話す（少し脱線可）」＋「次の曲紹介には踏み込まない」。
    (4)=`comment_target_chars`、(7)=`comment_short_target_chars`（(7) < (4)）。
  - 日付・季節節（`dateContext` 非空時）＋ system プロンプトは `MakeRequest` と同一パターン。
- **文字数下限縛り**: 各 LLM パートは既存の「N 文字以上 N×12/10 文字以内」（整数演算）を踏襲。`targetCharacters` は必須（既定 0 のままだと無意味なので呼び側で必ず > 0 を渡す。§18-8）。
- **4 行下限対策**: 導入・紹介・感想はメイン＋サブの対話として生成し 4 行下限を満たすが、**パート別 `Parse` は `minLines: 2`**（[Parse は既に `minLines` 引数あり](../../AIRadio.Core/DialogueScriptGenerator.cs)。既定 4 ではなく 2 を渡す。§18-9）。
- **(10) 締め**: 「以上、{artist}特集でした。」を含む**固定行**（LLM 生成しない＝4 行下限に抵触するため）。`ArtistFeatureParams.OutroLine`、`{artist}` は準備時に実名展開し、メインの `DialogueLine` として直接積む。`DialogueScriptGenerator` は締めを作らない。
- **挨拶抑制**: 特集は番組途中なので挨拶・自己紹介・番組名の名乗りはしない（`MakeArtistFeatureRequest` は greeting 引数を持たず構造的に抑止）。
- **1 曲ずつの曲振りはしない**（グループ単位でまとめて紹介 → 連続再生）。
- **季節・日付**: `SeasonPhrases.DateContext(_clock.Now, _timeZone)`（`CornerEngine.PrepareAsync` と同じ）を各パートのプロンプトへ。

---

## 8. 連続再生・完全静寂・先行準備・ED 早終了

### 8-1. 連続再生（CLAUDE.md §3-1）

- グループ内の各曲は **`PlayAsync(uri) → SetVolumeAsync(corner.Volume) → SongStarted → (play_seconds>0 ? DelayAsync : WaitForTrackToFinishAsync→SongFinished)`** を順に実行
  （`CornerEngine` 単曲再生のループ適用。既定 `play_seconds=0` はフル再生・終端検知＝S10/S12 fix を流用）。
- **曲間は pause しない**（`WebApiSpotifyController.PlayAsync` は queue をアトミック置換＝`PlayAsync(next)` で直接切替）。曲間ギャップは `endPollSeconds` ＋ play レイテンシ程度。
- **各曲 play 直後に必ず `SetVolumeAsync(corner.Volume)`**（発話→曲・曲→曲で音量を確実に曲用へ戻す）。
- **グループの最後の曲の後に 1 回 `PauseAsync`**（次の発話パートの前に音楽を止める）。

### 8-2. 完全静寂の絶対保証

- 特集ランナー `RunAsync` は **正常・例外・キャンセルのいずれでも最後に必ず `PauseAsync`**（`CornerEngine.RunAsync` [:180-200](../../AIRadio.Core/CornerEngine.cs) と同じ
  `try { PerformAsync } catch { PauseIgnoringCancellationAsync(restoringVolume: corner.Volume); throw } PauseAsync`）。各 await 点で OCE を伝播。
- **全曲・全発話パートを 1 つの try/catch で包む**。連続再生中のキャンセルは「最後に play した URI」を pause すれば一意に止まる（`run` は現在再生中 URI を追跡）。
  どの曲・どの await でキャンセルされても catch の `PauseIgnoringCancellationAsync` 1 回で現在音が止む（停止から 1 秒以内に無音）。
- **音量復元先は `corner.Volume`**（コーナー単位の音量規約。実運用 100）。

### 8-3. 先行準備（重い準備）

- 特集セグメントは 1 つで「最大 7 曲 top-tracks + 重複除外 + 複数パート台本 + 全行 TTS 事前合成」と通常 talk の数倍〜十数倍の準備量を持つ。
- **Win 実装方針＝Mac コードに忠実: 既存ローリング窓（`PreparationWindow=2`）に乗せる**。特集は body 5（OP→冒頭曲→talk→talk→letter→news→guest の後）にあり、
  **直前の長いゲスト talk セグメントがバッファになる**ため窓 2 で間に合う（Mac コードも開始時の特別な先出しを持たず、ローリング窓に依存）。
  - **Mac s15 §8-3 の「放送開始時に先行起動」という prose は実コードと相違**するため、Win では「ローリング窓 2 ＋ 直前ゲスト talk のリードタイムで間に合う」と読み替える（§18-10）。
  - 準備内部の並行化（`Task.WhenAll` で top-tracks/各パート台本/各行 TTS）は **任意の最適化**。**忠実な最小実装は逐次 `await`**（Mac shipped も逐次。決定論が要るのは選定 RNG のみ）。
  - 「放送開始前の無音は許容、放送中の長い無音ゼロ」に従い、到達時未完了なら待つ。実機で許容デッドエアを観測し、間に合わなければ先出し追加を後日再検討（観測フォローアップ）。

### 8-4. 特集スキップの契約（決定論 plan × 実行時スキップ）

`ProgramPlan` は決定論的に**特集セグメントを必ず 1 個**置く（§3）。実際に流すかは実行時の**プール有無**と **K** に依存する。

- **plan = 必ず置く / engine = 実行時に握りつぶす**。
- **プール空（artists.yaml 未生成）**: 放送開始時に選定できないので `selectedArtist = null` → `PrepareAsync(artist: null)` が**スキップ状態**の
  `PreparedArtistFeature` を返す（reason `E-ART-EMPTY-POOL-001`）。**ゲストと異なり fail-fast しない**（§18-3）。
- **準備で K ≤ 2 と判明**: スキップ状態（reason `E-ART-INSUFFICIENT-TRACKS-001`）。
- `PerformAsync` の `ArtistFeature` は、`RunAsync` がスキップ状態なら **`ArtistFeatureEvent.FeatureSkipped(reason)` を出して音を出さず即次へ**。
  - **`SegmentStarted` は通常どおり出る**（Mac s15 §8-4 prose「segmentStarted を出さず」は実コードと相違。`RunSegmentAsync` は全セグメントで `SegmentStarted`/`SegmentFinished` を出す。
    スキップは**ランナー内部の `FeatureSkipped` のみ**で表す。§18-11）。
  - **`SegmentFailed` には載せない**（事故ではない。特集セグメントの `Critical` は常に false）。
- 縮約（K=3〜6）はエラーではなく正常動作（情報ログのみ）。

### 8-5. ED 早終了との相互作用

既存 ED 早終了（[BroadcastEngine.cs:235-255](../../AIRadio.Core/BroadcastEngine.cs)）は「現セグメント＋準備完了済みの**直後 talk**（`Kind==Talk && CornerId==TalkCornerId`）だけ流して ED」。

- **特集再生中に ED**: 特集は 1 ブロックとして**最後まで流し切ってから ED**（既存規約と整合）。**特集の直後 talk を keeping 対象にしない**（特集後に余分なトークが挟まるのを防ぐ）。
  実装は「直前に流したセグメントが `ArtistFeature` のとき『直後 talk を流す』分岐に入らず ED 直行」＝[:239](../../AIRadio.Core/BroadcastEngine.cs) の trailing-talk ガード先頭に
  `segment.Kind != SegmentKind.ArtistFeature &&` を追加。
- **news 直後に ED**（特集・ゲストがまだ開始前）: 既存どおり `CancelAll()` で破棄して ED（許容）。
- **停止（Stop / Ctrl-C）**: §8-2 のキャンセル経路で即時無音。

---

## 9. `artists.yaml` と LLM 生成（トレイメニューの項目）

### 9-1. `config/artists.yaml`（新規、コミット可・機密でない、出荷時空）

```yaml
artists:
  - id: artist_001       # 安定 id（重複排除キー）。生成が連番採番（§9-3）
    name: "米津玄師"       # 表示・検索・台本に使う正式名
```

- 各エントリは **id（一意）/ name（必須）**。`ArtistProfile`（§10）に対応。曲は持たない（実行時 top-tracks で確定）。
- **`reading`（TTS 読み）は W15 では持たない**（Mac s15 §16 / W19b 送り。§18-12）。
- **出荷時は空**（`artists: []`）。空のときは特集スキップ（§8-4）。

### 9-2. `ArtistsConfig` ローダ（新規、Infra）

`GuestsConfig`（[GuestsConfig.cs](../../AIRadio.Infrastructure/Config/GuestsConfig.cs)）の `static class` 形を踏襲し `IReadOnlyList<ArtistProfile>` を返す。
**ただし空のルールを反転**（W14 ゲストと異なる＝§18-3）:

- **空・未生成は正常**: ファイル不在 / `artists:` 不在・空 / コメントのみ → **空リストを返す**（throw しない）。
  - `LoadFile(path)` は **`File.Exists` を確認**して不在なら空（他ローダの素 `ReadAllText` と異なる契約）。`FromYaml` は YamlDotNet が null `Dto`/null/空 `Artists` を返す場合を防御的に空扱い（コメントのみ→空の単体テスト必須）。
- **壊れているのは fail-fast**: パース不能 / name 欠落 / id 欠落 / **id 重複** / **正規化 name 重複** → `ConfigException.MissingField(...)`
  （`CanonicalName` = `Trim().ToLowerInvariant()` ＋ 全角/半角空白除去）。

### 9-3. トレイメニュー「アーティスト一覧を生成」項目（生成手段はこれに一本化）

- `TrayController`（[TrayMenu/TrayController.cs](../../AIRadio.App/TrayMenu/TrayController.cs)）に **`NativeMenuItem "アーティスト一覧を生成"`** を追加（長さサブメニューの後）。
  生成ロジックは `ArtistListGenerator`（Infra）に集約。**トリガはこの項目のみ**（CLI デモ `gen-artists` は作らない）。
- **状態ゲート（App 側排他、`BroadcastSession` は拡張しない＝§18-13）**: `TrayController` に `bool _isBroadcasting` / `bool _isGeneratingArtists` / `Task? _generateTask` / `CancellationTokenSource? _generateCts` を持つ。
  - Avalonia に `validateMenuItem` 相当が無いため**命令的に `IsEnabled` を更新**する `RefreshMenuEnablement()` を新設し、`Apply(state)`・生成開始・生成完了から呼ぶ（§18-14）:
    - `_generateArtistsItem.IsEnabled = !_isBroadcasting && !_isGeneratingArtists;`
    - `_toggleItem.IsEnabled = _isBroadcasting || !_isGeneratingArtists;`（生成中は「放送を開始」無効＝相互排他）
  - `Apply(state)` で `_isBroadcasting = state == Broadcasting;` を設定し `RefreshMenuEnablement()`。`Toggle()` 冒頭に `if (_isGeneratingArtists) return;`。
  - 生成は `Task.Run` の背景 Task。UI 反映（ヘッダ「アーティスト生成中…」、`IsEnabled`、完了表示）は **`Dispatcher.UIThread.Post`** でマーシャル（`OnStateChanged` と同じ）。フラグは UI スレッド所有。
- **生成設定は `config/artist-gen.yaml`** から読む（§13。`genre_prompt` 既定=邦楽 / `target_count` 既定=100）。
- **動作（`ArtistListGenerator`、Infra・具象クラス・単一消費者ゆえ interface 無し）**: ctor `(ILLMBackend llm, IArtistCatalog catalog, double temperature = 1.0)`。
  `Task<int> GenerateAsync(ArtistGenConfig config, string path, int maxRetries = 2, CancellationToken ct = default)`:
  - `genre_prompt` でアーティストを LLM に挙げさせる（重複・別表記回避、`target_count` を見越して多め要求）。`ct.ThrowIfCancellationRequested()` を各ステップに。
    **LLM へは Mac s15 §9-3 同様『1 行 1 組＝アーティスト名のみ』を要求**する。Mac 実装 `ArtistListGenerator.swift` が持つ『名前 TAB 全角カタカナ読み』形式・`parseEntries` の reading 検証・`VoicevoxUserDict` 依存は読み=W19b/辞書=W19a の前借りゆえ **W15 では移植しない**（reading を持たない＝§9-1/§18-12 と整合）。
  - **去重**: 正規化 name で去重（`ArtistsConfig.CanonicalName` 相当）。
  - **実在検証**: 去重後、各 name で `IArtistCatalog.TopTracksAsync(name, limit: 1, ct)` を 1 回かけ、**曲がヒットしないエントリは捨てる**。
  - 1 ラウンドで増えなければ break。最終 0 組なら `ArtistGenException`（**`RadioException` 派生でない**ツール専用例外。**`AIRadio.Infrastructure/ArtistListGenerator.cs` に同居宣言**＝Core/Errors.cs には置かない。日本語メッセージ文字列もそこで定義＝マジック文字列にしない。Mac は `ArtistGenError` を `ArtistListGenerator.swift` 同居）→ **ファイル不変**。
  - **id 採番**: 連番 `$"artist_{i+1:D3}"`。**id/name のみ書く**（reading は書かない＝§9-1）。
  - **上書き**: 既存 `artists.yaml` は**常に全置換**。原子的に: 一意名の一時ファイルへ書き（YamlDotNet `SerializerBuilder` + `UnderscoredNamingConvention` + null 省略、ヘッダコメントは手で前置、**UTF-8 BOM なし**）→ **`File.Replace`（既存時、NTFS 原子的）/ 初回は `File.Move(tmp, path, overwrite: true)`**（§18-15）。`File.WriteAllText` 直書きは禁止（途中クラッシュでプール破損）。
  - **失敗時**: LLM 不調・検証全滅などは**ファイル不変**＋ `IRadioLog` ＋ `_stateItem.Header` で表示（`NSAlert` 相当は無い。§18-16）。完了/失敗まで放送開始はブロック。
- **生成の composition**: `ArtistListGenerator` は Infra、それに `ILLMBackend`+`IArtistCatalog`+`ArtistGenConfig` を供給する factory は `BroadcastComposition` を拡張（新 composition クラスは作らない＝§3-5）。`DemoRunner` は無改修（CLI 生成なし）。
- **単一インスタンス**: W15 スコープ外（design.md の W-Win）。一時ファイル名を一意にして temp 衝突を避け、二重起動時の `artists.yaml` レースは許容（§18-17）。

---

## 10. 新規 Core/Infra 型と既存パターンの流用

### 10-1. Core（`AIRadio.Core`）

新規ファイル `AIRadio.Core/ArtistFeature.cs`（Mac `ArtistFeature.swift` 相当）に集約:

- `public sealed record ArtistProfile(string Id, string Name);`（reading は持たない＝§9-1）。
- `public abstract record ArtistFeaturePart { Intro / GroupIntro(IReadOnlyList<TrackInfo> Tracks, int Index, int Total) / Comment(bool Shorter) }`（§7）。
- `public abstract record ArtistFeatureEvent` nested: `ArtistSelected(string Name)` / `TracksPrepared(int Count)` / `PartScriptReady(int LineCount, int TotalCharacters)` / `LeadIn(string Text)` / `Line(DialogueLine DialogueLine)` / `SongStarted(TrackInfo Track)` / `SongFinished(TrackFinishReason Reason)` / `FeatureSkipped(string Reason)`（**実コードの形**＝Mac prose の `partScriptReady(partIndex,…)` ではない。§18-18）。
- `public sealed record PreparedArtistFeature(...)`（**split フィールド**＝flat な `partScripts/partAudio` ではない。§18-19）:
  `CornerTemplate Corner, ArtistProfile? Artist, IReadOnlyList<IReadOnlyList<TrackInfo>> Groups, DialogueScript IntroScript, IReadOnlyList<byte[]> IntroAudio, IReadOnlyList<DialogueScript> GroupIntroScripts, IReadOnlyList<IReadOnlyList<byte[]>> GroupIntroAudio, IReadOnlyList<DialogueScript> CommentScripts, IReadOnlyList<IReadOnlyList<byte[]>> CommentAudio, DialogueLine OutroLine, byte[] OutroAudio, IReadOnlyList<string> CastDjIds, string? LeadIn, int LeadInSpeakerId, bool Skipped = false, string? SkipReason = null` ＋ static `Skip(corner, castDjIds, reason)` ファクトリ。
- `public interface IArtistCatalog`（§5）。**唯一の新規 interface**。
- `public sealed class ArtistFeatureEngine`（**具象・interface 無し**＝`CornerEngine` 同様の単一消費者。`IArtistFeatureRunner` は作らない＝§18-20）:
  - ctor 注入 `(ILLMBackend llm, ITTSBackend tts, IAudioPlayer audio, IArtistCatalog catalog, ISpotifyController spotify, IClock clock, double temperature = 0.9, TimeZoneInfo? timeZone = null, Action<ArtistFeatureEvent>? onEvent = null)`。定数 `FetchLimit=10/TargetTracks=7/MinTracks=3`。**RNG は持たない**（選定は BroadcastEngine）。
  - `Task<PreparedArtistFeature> PrepareAsync(CornerTemplate corner, ArtistProfile? artist, IReadOnlyList<DjProfile> djs, IReadOnlyList<string> castDjIds, string? leadIn, CancellationToken ct = default)`（**実コードのシグネチャ**＝dateContext は内部で `SeasonPhrases.DateContext`、onEvent は ctor。§18-21）:
    cast 解決→`artist==null` なら `Skip(EMPTY-POOL)`→top-tracks→`Deduplicate`→`Take(7)`（`TracksPrepared`）→`K<3` なら `Skip(INSUFFICIENT)`→`SplitGroups`→パート別台本（逐次 `MakeArtistFeatureRequest`→`llm`→`Parse(minLines:2)`）→`OutroLine` の `{artist}` 置換→全 audio 事前合成→`leadIn` の `{artist}` 埋め（時刻は残す）。
  - `Task RunAsync(PreparedArtistFeature prepared, IReadOnlyList<DjProfile> djs, CancellationToken ct = default)`: skip なら `FeatureSkipped` を出して return（音なし）。else §8-2 の try/catch で `PerformAsync`（leadIn 発話直前展開→intro→各グループ(紹介→`PlayGroupAsync`→(最後でなければ)感想)→outro）。`PlayGroupAsync` は §8-1。
  - private `PerformAsync`/`PlayGroupAsync`/`SpeakAsync`/`GeneratePartAsync`/`SynthesizeAsync`/`ResolveCast`/`SpeakerIdFor`、static `Deduplicate`/`CanonicalTitle`/`SplitGroups`。`PauseIgnoringCancellationAsync`/`WaitForTrackToFinishAsync` 拡張を流用。
- `DialogueScriptGenerator.MakeArtistFeatureRequest`（§7、`Dialogue.cs` 側に `ArtistFeatureParams` も追加、CornerTemplate に `ArtistFeatureParams? ArtistFeatureParams = null` 末尾追加）。
- `Dialogue.cs`: `public sealed record ArtistFeatureParams(int IntroTargetChars = 200, int GroupIntroTargetChars = 320, int CommentTargetChars = 400, int CommentShortTargetChars = 240, string OutroLine = "以上、{artist}特集でした。");`

### 10-2. BroadcastEngine（[BroadcastEngine.cs](../../AIRadio.Core/BroadcastEngine.cs)）

- **ctor**: `ArtistFeatureEngine? artistFeatureRunner = null` を **末尾**（`randomIndex` の後）に追加（positional テスト呼出を壊さない＝W14 の `randomIndex` 末尾追加と同方針。§18-22）。`CornerEngine cornerRunner` と同じく具象注入。
- **`RunAsync`**: `IReadOnlyList<ArtistProfile>? artists = null` を **`guests` の後・`ct` の前**に追加。全呼出は名前付き `guests:`/`artists:`/`ct:`。
- **fail-fast 検証＋選定**（guest ブロック [:123-148](../../AIRadio.Core/BroadcastEngine.cs) の直後）: `Blueprint.ArtistFeatureCornerId is string afId && plan.IncludesArtistFeature` のとき
  (a) 特集 corner template 存在（不在は `ConfigException.MissingField("artist_feature.corner_id (template missing)")`）、
  (b) `afId` が talk/letter/guest の id いずれとも異なる（衝突は `ConfigException.MissingField("artist_feature.corner_id (collision with …)")`）、
  (c) `artistFeatureRunner` 非 null（null は `ConfigException.MissingField("artistFeatureRunner 未配線")`）を検証。
  **プール非空のときのみ** `selectedArtist = artistPool[_randomIndex(artistPool.Count)]`（空は null のまま＝スキップ。§18-3）。`field` 文字列で 3 種を区別（§18-23）。
- **`selectedArtist` を `BroadcastAsync`→`EnsurePrepared`→`StartPreparation` へ引き回す**（guest と同様、`ArtistProfile? artist` 引数追加）。lead-in は `featureCorner.LeadIn` を**直接**渡す（`CornerContext` は talk 専用のまま、特集には使わない＝§18-24）。
- **`StartPreparation` に `case SegmentKind.ArtistFeature`**: `featureCorner = corners.First(c => c.Id == segment.CornerId)`、`BeginPreparation(index, ct)`、`ledger.AddArtistFeature(index, PrepareArtistFeatureAsync(featureCorner, artist, djs, castDjIds, featureCorner.LeadIn, token))`。
- **`PerformAsync` に `case SegmentKind.ArtistFeature`**: `ledger.ArtistFeatureTask(index)` を await（無ければ `BroadcastException.SegmentFailed`）→ `artistFeatureRunner.RunAsync(prepared, djs, ct)`（runner null は `SegmentFailed`）。skip は runner 内部。
- **`PreparationLedger`**（[:519-615](../../AIRadio.Core/BroadcastEngine.cs)）: `Dictionary<int, Task<PreparedArtistFeature>> _artistFeatureTasks` ＋ `AddArtistFeature`/`ArtistFeatureTask`。**`Discard` の Remove と `DrainAsync` の観測リストの両方に追加**（Win は per-index linked CTS を Dispose するため、漏らすと未観測例外＋CTS リーク。§18-25）。
- **ED 早終了**: §8-5 の `segment.Kind != SegmentKind.ArtistFeature &&` を trailing-talk ガードに追加。

### 10-3. Infra / App

- Infra: `SpotifyArtistCatalog`（§5）、`ArtistsConfig`（§9-2）、`ArtistGenConfig`（§13、record + `LoadFile`）、`ArtistListGenerator`（§9-3）。
  `ProgramConfig`（`ProgramDto` に `TalkDto? ArtistFeature` 追加→`ArtistFeatureCornerId` を nil-passthrough。`artist_feature` を IgnoreUnmatchedProperties で捨てる現状[:11,133](../../AIRadio.Infrastructure/Config/ProgramConfig.cs)を解除）。
  `CornersConfig`（`CornerDto` に `intro_target_chars`/`group_intro_target_chars`/`comment_target_chars`/`comment_short_target_chars`/`outro_line` を追加。`format==ArtistFeature` のとき `ArtistFeatureParams` を構築し **`CommentShortTargetChars >= CommentTargetChars` を fail-fast 検証**。`outro_line` 空文字は既定にフォールバック=`IsNullOrEmpty`。§18-26）。
- App: `BroadcastComposition` に `SpotifyArtistCatalog` ＋ `ArtistFeatureEngine` を DI 配線し、`artists.yaml` を任意ロード（無ければ空）→`engine.RunAsync(..., guests:, artists:, ct:)`。生成 factory（§9-3）。**`ArtistFeatureEngine` は `onEvent: e => _log.Log(FormatArtistFeatureEvent(e))` で配線必須**（`CornerEngine`/`BroadcastEngine` の onEvent 配線と同形＝[BroadcastComposition.cs](../../AIRadio.App/Composition/BroadcastComposition.cs) の既存 `FormatCornerEvent`/`FormatBroadcastEvent` パターン）。`FormatArtistFeatureEvent` switch 式を新設（`FormatCornerEvent` と同形・末尾 `_ => ""`・全 8 case 網羅 ArtistSelected/TracksPrepared/PartScriptReady/LeadIn/Line/SongStarted/SongFinished/**FeatureSkipped**）。**onEvent を null のままにすると FeatureSkipped（ART コード）がどこにも出力されず §11/§15 を満たせない**（§18-32。Mac `printArtistFeatureEvent` 相当）。`TrayController` の生成項目＋状態ゲート（§9-3）。
- config: `program.yaml`（`artist_feature.corner_id: artist_feature`）、`corners.yaml`（`artist_feature` コーナー）、`artist-gen.yaml`（新規・コミット）、`artists.yaml`（出荷時空）。

---

## 11. エラーコード

新カテゴリ **ART（アーティスト特集）**は**実行時の特集スキップ専用**。起動時の設定不正は既存 **CFG** に寄せる。
**`docs/specs/error-codes.md`・`CLAUDE.md §4-3` は既にミラー済み＝編集不要**（ART カテゴリ・両 ART 行・方針段落が存在）。本スライスの実コード差分のみ:

| コード | カテゴリ | 発生条件 | 扱い |
|---|---|---|---|
| `E-ART-INSUFFICIENT-TRACKS-001` | ART | 再生可能曲が最低 3 曲未満（K ≤ 2）→ 特集スキップ | **fail-tolerant・throw しない**。`FeatureSkipped(reason)` のログ用安定コード |
| `E-ART-EMPTY-POOL-001` | ART | `artist_feature.corner_id` 設定だが artists.yaml が空／未生成 → 特集スキップ | **fail-tolerant・throw しない**。`FeatureSkipped(reason)` のログ用安定コード |
| `E-CFG-MISSING-FIELD-001`（既存） | CFG | corner template 不在 / id 衝突（talk/letter/guest）/ artists.yaml が**壊れている**（空は除く）/ runner 未配線 | fail-fast（`ConfigException`） |

- **ART コードは throw しない・`RadioException` 派生を作らない**（§18-27）。Win では `Errors.cs` に **`public static class ArtistFeatureErrors`** を置き、
  `public const string EmptyPool = "E-ART-EMPTY-POOL-001";` / `public const string InsufficientTracks = "E-ART-INSUFFICIENT-TRACKS-001";` と reason ビルダ（`EmptyPoolReason()` / `InsufficientTracksReason(int count, int min)` ＝コードを含む日本語文字列）を公開（§3-3 のマジック文字列回避）。
- スキップは `ArtistFeatureEvent.FeatureSkipped(reason)` ＋情報ログに一本化し、**`BroadcastEvent.SegmentFailed`（[:13](../../AIRadio.Core/BroadcastEngine.cs)、`Code` 引数を持つ）には載せない**（§18-28）。特集セグメントの `Critical` は常に false。
- 縮約（K=3〜6）はエラーでなく正常（情報ログのみ）。プレフライト個別失敗はコード化しない（既存 fail-tolerant）。

---

## 12. テスト方針（決定論・fake 差し替え、CLAUDE.md §3-5）

`dotnet test AIRadio.slnx` 全グリーンを完了条件とする。`AIRadio.TestSupport/Fakes.cs` に **`FakeArtistCatalog : IArtistCatalog`** を追加（既存 `ScriptedLLM`/`InMemoryTTS`/`SpyAudioPlayer`/`FakeSpotifyController`/`FakeClock` は流用）。

- **`ProgramPlanTests`**（[ProgramPlanTests.cs](../../AIRadio.Core.Tests/ProgramPlanTests.cs)）: `ArtistFeatureCornerId` 設定で N=2/3(奇数)/4/5/エンドレスに「ゲスト直後 1 個だけ」特集挿入、2 回目 news 後は無し、guest 無効で特集無し、`TotalSegmentCount` +1（guest と +2、エンドレス null）、N に数えない、index ずらし。tuple ヘルパ `Tfeat = (SegmentKind.ArtistFeature, "artist_feature")` 追加。**W14 ゲストテストは不変**（`InsertionsBefore` 一般化の回帰グリーン）。
- **`BroadcastEngineTests`**: `randomIndex: _ => 0` で 1 組決定論選定→prepare に渡る、fail-fast（template 不在 / id 衝突 / 壊れ yaml / runner 未配線）、1 放送ちょうど 1 回、**K ≤ 2 → `FeatureSkipped`（`E-ART-INSUFFICIENT-TRACKS-001`、`SegmentFailed` が出ないことをアサート）＋継続**、**プール空 → `FeatureSkipped`（`E-ART-EMPTY-POOL-001`）＋継続**、ED 早終了（特集再生中の ED で特集を流し切り→ED・余分なトーク無し / news 直後 ED の破棄は許容）。`BuildEngine` に `ArtistFeatureEngine?`（または fake collaborators で実 `ArtistFeatureEngine`）追加。
- **`SpotifyArtistCatalogTests`**（Infra）: fake http で resolve→top-tracks、両リクエストの market=JP / Bearer、未解決→空リスト（**2 回目 HTTP 呼ばず**・throw なし）、`HttpStatusException`→`E-SPT-SEARCH-FAILED-001`。
- **`ArtistFeatureEngineTests`**（fake llm/tts/audio/catalog/spotify/clock）: 7→3+3+1 分割、縮約（6/5/4/3/≤2）と感想回数、リード文 {artist} 置換＋時刻は run 時、パート別台本（(7)<(4) / 紹介に確定曲名が原文一致 / グループ紹介の位置依存（1 回目素直・2 回目連続感・最後ラスト明示）/ 締め固定行「{artist}特集でした。」の実名展開）、連続再生の曲順・曲数・**各曲 play 後 setVolume**、キャンセル 3 点（1 曲目再生中 / 1→2 遷移直後 / 3 曲目再生中）でいずれも `PauseIgnoringCancellationAsync` 呼出＋1 秒以内無音（fake spotify の pause 記録で検証）、例外でも必ず pause、停止後の音量復元。K=4 の「3 pause（グループ [3,1] 各末＋run 末）」相当を移植。
- **`ArtistsConfigTests`**（Infra、`GuestsConfigTests` 模倣）: 正常 / 必須欠落(name) / id 重複 / 正規化 name 重複 / 壊れ yaml→throw(`E-CFG-MISSING-FIELD-001`) / ファイル不在→空リスト / コメントのみ→空リスト。
- **`ArtistGenConfigTests`**（Infra）: 既定（不在/空）、`genre_prompt`/`target_count` 読取、`target_count < 1` は既定にクランプ（§18-29）。
- **`ArtistListGenerator` テスト**（Infra）: 去重＋実在検証（fake catalog）、原子的上書き、**実在検証で全滅(0組)時に `ArtistGenException` を throw し artists.yaml が不変**であることをアサート、キャンセル。
- **`DialogueScriptGeneratorTests`**: artistFeature パートの曲名原文一致・締め・位置依存（index 0/total 3 素直 / index 1/total 3 連続感 / index 2 ラスト「最後はこの 1 曲」/ index1/total2「最後はこの 2 曲」/ index 0/total 1 唯一グループ素直）・anti-show-close 句、guest/letter/freeTalk 既存不変（回帰）。
- **`ProgramThemesConfigTests`**: `Program_IgnoresArtistFeature` を **`Program_ReadsArtistFeatureCornerId`** に置換（§18-30）＋省略時 null。
- **`DjsCornersConfigTests`**: artist_feature format→params 構築・既定適用・`comment_short >= comment` で throw。
- **`ErrorsTests`**: ART 2 定数の安定コード（reason がコードを含む）。
- **先行準備**（fake clock）: 特集準備が特集到達前に完了。
- **回帰（不変）**: W14 ゲスト挿入、完全静寂、ローリング準備、ED ボタン、時報リード文の時刻正確性、W13.5 曜日替わり、`BroadcastSessionTests`（**不変**＝§18-13）。

---

## 13. 設定（命名規約）

- `config/artists.yaml`（kebab-case、**出荷時空**、生成で上書き）。キー `artists`/`id`/`name`（snake_case）。
- `config/artist-gen.yaml`（kebab-case、**コミット済み＝入力**）。`generation.genre_prompt`（既定 邦楽）/ `generation.target_count`（既定 100）。market は `spotify.local.yaml` を流用（本ファイルに持たない）。
- `program.yaml`: `artist_feature.corner_id`（W14 の `guest.corner_id` と並ぶ）。
- `corners.yaml`（`id: artist_feature`、`format: artist_feature`）: `lead_in`（{artist}＋時刻）/ `outro_line`（既定「以上、{artist}特集でした。」）/ パート別文字数 `intro_target_chars`(200)/`group_intro_target_chars`(320)/`comment_target_chars`(400)/`comment_short_target_chars`(240) / `volume` / `play_seconds`。**`comment_short < comment` をロード時 fail-fast 検証**。
- コード識別子は英語、日本語表示文字列は YAML に集約。機密値なし。

---

## 14. 実装チェックリスト

- [ ] Core: `SegmentKind.ArtistFeature`（末尾）＋網羅 switch 手動監査。`ProgramBlueprint.ArtistFeatureCornerId`。
- [ ] Core: `ProgramPlan`（`IncludesArtistFeature` / `ArtistFeatureBodyPosition` / `InsertionsBefore` 一般化 / body5 挿入 / `TotalSegmentCount` / エンドレス）。
- [ ] Core(`ArtistFeature.cs`): `ArtistProfile` / `ArtistFeaturePart` / `ArtistFeatureEvent` / `PreparedArtistFeature` / `IArtistCatalog`（Abstractions.cs）/ `ArtistFeatureEngine`（具象）。`Dialogue.cs`: `ArtistFeatureParams` ＋ `CornerTemplate.ArtistFeatureParams`。
- [ ] Core: `DialogueScriptGenerator.MakeArtistFeatureRequest`（曲配列・原文一致・位置依存・(7)<(4)・途中句は Win 長形）。締めは固定行。
- [ ] Core: `BroadcastEngine`（ctor 末尾 runner / `RunAsync(artists:)` / fail-fast＋選定（空プールは skip）/ `StartPreparation`・`PerformAsync` の `ArtistFeature` case / `PreparationLedger` 特集タスク（Add/Task/Discard/Drain）/ ED 早終了分岐）。
- [ ] Infra: `SpotifyArtistCatalog` / `ArtistsConfig`（空=空・壊れ=throw）/ `ArtistGenConfig` / `ArtistListGenerator`（原子的上書き）。`ProgramConfig`・`CornersConfig` 拡張。
- [ ] App: `BroadcastComposition`（catalog・engine 配線〔`onEvent` を `_log.Log(FormatArtistFeatureEvent(e))` へ〕、`FormatArtistFeatureEvent` 追加、artists.yaml 任意ロード、生成 factory）。`TrayController`（生成項目＋App 側排他＋`RefreshMenuEnablement`）。`DemoRunner` 無改修。
- [ ] Core: `Errors.cs` に `ArtistFeatureErrors`（定数＋reason ビルダ、**throw しない**）。
- [ ] config: `program.yaml` / `corners.yaml` / `artist-gen.yaml`（コミット）/ `artists.yaml`（空）。
- [ ] docs: `error-codes.md`・`CLAUDE.md §4-3`・`design.md:152` は**確認のみ（編集不要）**。
- [ ] Tests: §12 一式。`dotnet test AIRadio.slnx` 全グリーン・0 警告。

---

## 15. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。新規 ART 2 コードを `ErrorsTests` で検証。
- 放送中、ゲストコーナー直後に 1 回だけ特集が入り、「{時刻}になりました。ここからはアーティスト特集です。本日は〈アーティスト〉さんを特集します」で始まり、§4(1)〜(10)（または §6 縮約形）で進み、「以上、〈アーティスト〉特集でした。」で締める。2 回目以降のグループ紹介は「続いて」等で前を受け、最後のグループは「最後はこの N 曲」とラストを添える。（**実機確認＝ユーザー**）
- 名指しした曲（最大 7・縮約後の実曲）はすべてプレフライト済みで実際に流れる（紹介と再生が一致、曲名一致）。
- K < 3 で特集スキップ・`E-ART-INSUFFICIENT-TRACKS-001` をログに残して放送継続（`SegmentFailed` を出さない）。プール空で `E-ART-EMPTY-POOL-001` スキップ＋継続。
- 特集は 1 放送 1 回（2 回目以降の news 後には入らない）。
- 連続再生で曲間に発話・長い無音がなく、停止時は 1 秒以内に無音。
- トレイ「アーティスト一覧を生成」（放送停止時のみ有効）で `artist-gen.yaml` の `genre_prompt`/`target_count` に従い `artists.yaml`（去重・実在検証済み）が**原子的に上書き**される。生成中は放送開始無効。失敗時ファイル不変。（**実機確認＝ユーザー**）

---

## 16. スコープ外（将来）

- 特集の**位置可変・複数回**（現状ゲスト直後固定・1 回）。
- K ≤ 2 時の**別アーティスト引き直し**（現状スキップ）。
- アーティストの**テーマ／ジャンル連動選定**（現状ランダム）。
- スキップ時の UI カウント補正、`TrackFinishReason` の UI 露出。
- `artists.yaml` への `reading`（TTS 読み）追加 ＝ **W19b**。
- 単一インスタンス（Mutex）＝ **W-Win**。

---

## 17. Mac リファレンス

`Sources/AIRadioCore/ArtistFeature.swift`（完全な参照実装）/ `Program.swift`（§3）/ `DialogueScriptGenerator.swift:151-218`（`makeArtistFeatureRequest`）/ `BroadcastEngine.swift`（統合）/ `Sources/AIRadioInfra/{SpotifyArtistCatalog,ArtistsConfig,ArtistGenConfig,ArtistListGenerator}.swift` / `Sources/AIRadioApp/{MenuBar,BroadcastWiring}.swift`（`printArtistFeatureEvent` = Win `FormatArtistFeatureEvent` の元） / `Tests/AIRadioCoreTests/{ProgramPlanArtistFeatureTests,ArtistFeatureEngineTests,BroadcastEngineArtistFeatureTests,DialogueScriptGeneratorTests}.swift`。SoT: `docs/specs/s15-artist-feature.md`。

---

## 18. Win 固有の確定判断（移植時の最優先・parity trap 集）

実装者が Mac prose や W14 の筋肉記憶で誤りやすい点を確定。各項は §の該当箇所と対。

1. **`SegmentKind.ArtistFeature` は末尾追加**（Mac は `Ending` 前）。序数 parity は取らない。
2. **enum 網羅は C# 非強制**。`StartPreparation`/`PerformAsync` の switch に明示 case を手で追加（落とすと沈黙バグ）。
3. **空プール非対称**: ゲストは空で fail-fast、**アーティストは空でも実行時スキップ**（`selectedArtist=null`、throw しない）。
4. **`IArtistCatalog` は interface**（真のシーム）。
5. **`TrackInfo` に duration を足さない**（Mac/Win とも不要）。
6. **`IsPlayable` 二重確認はしない**（Mac engine 準拠）。
7. **`MakeArtistFeatureRequest` の途中句は Win の長い anti-show-close 形**（Mac は短縮形。Win は `MakeRequest` と一貫させる。特集の締め「特集でした」はコーナー締めで anti-show-close と非矛盾）。
8. **`targetCharacters` は必須で > 0**（既定 0 のまま渡さない）。
9. **パート別 `Parse` は `minLines: 2`**（既定 4 でない）。
10. **先行準備は「開始時先出し」でなくローリング窓 2 ＋ 直前ゲスト talk のリードタイム**（Mac prose §8-3 は実コードと相違）。実機でデッドエア観測しフォローアップ。
11. **skip 時も `SegmentStarted` は出る**（Mac prose §8-4「出さず」は実コードと相違）。skip はランナー内部 `FeatureSkipped` で表す。
12. **`ArtistProfile` は id/name のみ**（reading は W19b）。
13. **`BroadcastSession` を拡張しない**。生成排他は `TrayController` の 2 フラグ（W9 `BroadcastSessionTests` 凍結を保護）。
14. **Avalonia に `validateMenuItem` 無し**→ `RefreshMenuEnablement()` で `IsEnabled` を命令的更新（`Apply`/生成開始/生成完了から呼ぶ）。
15. **原子的上書きは `File.Replace`（既存）/ `File.Move(overwrite)`（初回）**＋一意 temp 名＋UTF-8 BOM なし（POSIX rename でない）。
16. **失敗 UI は `IRadioLog`＋`_stateItem.Header`**（`NSAlert` 相当無し・メインウィンドウ無し）。
17. **単一インスタンスは W15 スコープ外**（W-Win）。temp 名一意化で衝突回避、二重起動レースは許容。
18. **`ArtistFeatureEvent` は実コードの形**（`PartScriptReady(LineCount, TotalCharacters)`＝partIndex 無し）。
19. **`PreparedArtistFeature` は split フィールド**（flat partScripts/partAudio でない）。
20. **`ArtistFeatureEngine` は具象・interface 無し**（`IArtistFeatureRunner` を作らない＝`CornerEngine` 前例・§3-5。BroadcastEngine テストは collaborators を fake）。
21. **`PrepareAsync` は実コードのシグネチャ**（dateContext は内部 `SeasonPhrases`、onEvent は ctor）。
22. **BroadcastEngine ctor の runner は末尾**（positional テスト非破壊）。`RunAsync` の `artists` は `guests` 後・`ct` 前、全呼出名前付き。
23. **CFG の 3 種は `field` 文字列で区別**（コードは同一 `E-CFG-MISSING-FIELD-001`）。
24. **特集 lead-in は `featureCorner.LeadIn` を直接**（`CornerContext` は talk 専用のまま）。
25. **`PreparationLedger` の特集タスクは Add/Task に加え `Discard` Remove・`DrainAsync` 観測の両方に追加**（CTS Dispose リーク回避）。
26. **`outro_line` 空文字は既定にフォールバック**（`IsNullOrEmpty`、`?? default` でない）。`comment_short < comment` 検証は **artist_feature format のときのみ**。
27. **ART コードは throw しない・`RadioException` 派生を作らない**（`ArtistFeatureErrors` 定数＋reason ビルダ）。
28. **skip を `BroadcastEvent.SegmentFailed`（`Code` 引数あり）に載せない**。
29. **`target_count < 1` は既定（100）にクランプ**（1 でも throw でもない）。
30. **`Program_IgnoresArtistFeature` テストを反転**（`Program_ReadsArtistFeatureCornerId`）。
31. **特集の日付コンテキストは `SeasonPhrases.DateContext`（季節のみ）に縮約**＝Mac s15 は `dailyCalendar.context()`（s17 由来の曜日名＋該当日の記念日）を各パートに渡すが、Win は `DailyCalendar`（W17）未移植のため `CornerEngine.PrepareAsync`（CornerEngine.cs:123）と同じ `SeasonPhrases.DateContext` に揃える＝**意図的な縮約**（曜日・記念日は台本から落ちる）。特集だけ DailyCalendar を先行移植しない。W17 移植時に CornerEngine と同時に差し替えて Mac parity を回復する。
32. **`ArtistFeatureEvent` はエンジン自身の `onEvent` チャネル**（`BroadcastEvent` ではない）。App の `BroadcastComposition` で `IRadioLog` へブリッジ必須（§10-3。`onEvent` を null のままにすると FeatureSkipped/Line/縮約が無出力＝§8-4・§11・§15・CLAUDE.md §2 を満たさない）。`FormatArtistFeatureEvent` は `FormatCornerEvent` と同形・末尾 `_ => ""`・全 8 case 網羅。
