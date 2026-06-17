# W14 — ゲストコーナー（1 放送 1 回、最初のニュースの直後に固定）

Mac 版 `s14`（`../AIRadio_Mac-main/docs/specs/s14-guest-corner.md`）の Windows 移植。
1 放送に 1 回だけ、**最初のニュース＋天気の直後**にゲストコーナーを挿入する。その日の編成（メイン＋サブ）＋
**ゲスト 1 名**の会話。ゲストは VOICEVOX の非レギュラーキャラのプールから**ランダム選定**するが、台本上は
**「本日のテーマに詳しいゲスト」という建付け**（テーマ連動の選定ロジックは持たない）。

> 進め方は確立パターン（CLAUDE.md §7）: 本 spec 凍結 → ユーザー承認 → 実装 → 多角レビュー Workflow →
> `dotnet test` 緑 → commit/push → Confluence ミラー。

---

## 0. スコープ

### 入れる（Mac s14 §2〜§5）
1. **ゲストプール** `config/guests.yaml`（新規）+ `GuestsConfig` ローダ → `IReadOnlyList<DjProfile>`。
2. **配置**: `ProgramBlueprint.GuestCornerId` + `ProgramPlan` が**最初の news の直後**にゲスト talk を 1 つ挿入（N≥2 / エンドレス）。`TotalSegmentCount` +1。ゲストは N に数えない。
3. **ゲストコーナー構造**: `CornerFormat.Guest`（既存）の実処理を `CornerEngine` に実装。cast 末尾にゲスト、リード文の `{guest}`/`{theme}` を準備時に埋め、時刻は発話直前展開。台本にゲスト挨拶・専門家フレーミング・お礼。
4. **選定**: `BroadcastEngine` が放送開始時にプールから 1 名選定（乱数注入でテスト決定論）。fail-fast（template 不在 / プール空 / レギュラー衝突 / id 重複）。
5. config: `guests.yaml`（初期 10 名）、`corners.yaml` に `guest` コーナー（format: guest + lead_in）、`program.yaml` に `guest.corner_id`。

### 入れない（後続スライスへ defer）
- **アーティスト特集**（`artist_feature` / `SegmentKind.ArtistFeature` / `ProgramPlan` の特集挿入）→ **W15**。
- ゲストのテーマ連動選定（タグマッチング）→ 将来（今回はランダム + 専門家フレーミング）。
- ゲストの感情スタイル（VOICEVOX style 切替）→ 将来（ノーマル固定）。

> Mac の `Program.swift`/`BroadcastEngine.swift`/`CornerEngine.swift` は W15（artistFeature）も蓄積済み。W14 は
> **ゲスト挿入のみ**を移植し、artistFeature 分岐（`artistFeatureBodyPosition`/`includesArtistFeature` 等）は持ち込まない（§3-5）。
> **既存**: `CornerFormat.Guest` は W6 から定義済み（`CornerEngine` は現在 throw。本スライスで実処理化）。`CornerContext`/`PreparedCorner`/lead_in 二層化は W13.5 済み。

---

## 1. ゲストプール（`config/guests.yaml` + `AIRadio.Infrastructure/Config/GuestsConfig.cs`）

- 構造は `djs.yaml` と同形（`guests:` キー → `id`/`name`/`speaker_id`/`persona`）。レギュラー（zundamon/metan/tsumugi/ryusei）を**除く** VOICEVOX 非レギュラーキャラ。
- `GuestsConfig.FromYaml/LoadFile` → `IReadOnlyList<DjProfile>`（`guests:` キー → `id`/`name`/`speaker_id`/`persona`）。
  - **fail-fast は `guests` 空 / 各 `id` / `name` / `speaker_id` 欠落のみ**（`E-CFG-MISSING-FIELD-001`）。**`persona` は任意（既定 ""）＝ここが `DjsConfig` と異なる**（`DjsConfig` は persona 必須。Mac `GuestsConfigLoader` 一致）。「DjsConfig を流用」する場合も persona 検証は外すこと。
- 初期メンバー（実装時に VOICEVOX `/speakers` で speaker_id 再確認。ユーザー編集可）:

| id | name | speaker_id | persona 要旨 |
|---|---|---|---|
| sora | 九州そら | 16 | おっとり穏やか、丁寧な敬語 |
| takehiro | 玄野武宏 | 11 | 爽やかで頼れる青年、はきはき |
| kotarou | 白上虎太郎 | 12 | 元気いっぱいの少年、好奇心旺盛 |
| himari | 冥鳴ひまり | 14 | 落ち着いた知的な女性、ふんわり |
| mochiko | もち子さん | 20 | マイペースでほんわか |
| no7 | No.7 | 29 | クールで理知的、アナウンサー的 |
| zunko | 東北ずん子 | 107 | しっかり者で優しいお姉さん |
| kiritan | 東北きりたん | 108 | クールで少しツンとした芯のある女の子 |
| itako | 東北イタコ | 109 | 落ち着いた大人の女性、包容力 |
| ankomon | あんこもん | 113 | **語尾に「もん」を付ける独特の口調**（甘いもの好き・素直・好奇心旺盛） |

- 伸ばす音は長音符「ー」、「〜」不可（VOICEVOX 読み）。persona に専門分野は書かない（建付けは台本側）。

---

## 2. 配置（`AIRadio.Core/Program.cs` の `ProgramPlan`）

- `ProgramBlueprint.GuestCornerId`（`string?`、`program.yaml` の `guest.corner_id`。null で無効）を追加。
- `ProgramPlan` の本編 body 生成にゲスト挿入を追加（Mac `bodySegment`/`guestBodyPosition`/`insertionsBefore` 移植、artistFeature 部は除外）:
  - `IncludesGuestCorner` = `GuestCornerId != null && (エンドレス || N >= 2)`。
  - **ゲスト body 位置 = 4**（本編パターン `t(0), t(1), letter(2), news(3)` の直後）。
  - `BodySegment(body)`: `body == 4 && IncludesGuestCorner` → `Talk(GuestCornerId)`。それ以外は `PatternBodySegment(body - insertionsBefore(body))`（`insertionsBefore` = ゲスト位置より後なら 1）。
  - 現行の `BodySegment`（パターン生成）を `PatternBodySegment` に改名し、新 `BodySegment` がゲスト挿入を被せる。
  - `TotalSegmentCount` = `2 + (N/2)*4 + (N%2) + 1 + (IncludesGuestCorner ? 1 : 0)`。
- 例（N=2）: `OP, song, t, t, letter, news, guest, ED`（8）。
  例（N=4）: `OP, song, t, t, letter, news, guest, t, t, letter, news, ED`（12。**2 回目 news 後はゲストなし**）。
- ゲストは **N（トーク数）に数えない**（letter/news と同じ）。`ProgramPlan` の生成は cast 非依存・決定論。

---

## 3. ゲストコーナーの実処理（`AIRadio.Core/CornerEngine.cs`, `Dialogue.cs`）

- `CornerContext` に `DjProfile? Guest` を追加（非 null＝ゲストコーナー、選定済みゲスト）。
- `PreparedCorner` に `DjProfile? Guest` を追加（run で cast 末尾に補う。ゲストは `djs` に居ないため別持ち）。
- `CornerEngine.PrepareAsync`（`CornerFormat.Guest` を未対応 throw から実処理へ）:
  - `guest = corner.Format == Guest ? context.Guest : null`。`cast = guest is null ? baseCast : baseCast + [guest]`（末尾）。`CornerEvent.GuestReady(name)` を発火（イベント追加）。
  - 選曲コンテキスト = ゲスト変種（`「{corner.Title}」（テーマ: {theme}）でゲストを迎えての会話の締めにかける曲`）。letter=null。
  - 台本生成に `guest` を渡す（§4）。
  - **lead_in の `{theme}`/`{guest}` を準備時に置換**してから `PreparedCorner.LeadIn` に格納（時刻プレースホルダのみ残す）。`{guest}`→`guest.Name`、`{theme}`→選択テーマ。**この置換は全コーナーの lead_in に適用**（free_talk/letter は `{theme}`/`{guest}` を含まないので無害）。
  - `PreparedCorner.Guest = guest`。`CastDjIds` は base cast ids（ゲストは含めない）。
- `CornerEngine.RunAsync(prepared, djs)`: cast 解決後 `prepared.Guest` を末尾に補う（準備時と同じ並び）。lead_in 発話直前展開・本編・締め曲は W13.5/既存と同じ。
- `SpeakerIdFor` はゲストを含む cast で解決（ゲスト行はゲストの speaker）。

---

## 4. 主導/応答にゲスト（`AIRadio.Core/DialogueScriptGenerator.cs`）

- `GenerateAsync`/`MakeRequest` に `DjProfile? guest = null` を追加。`djs`（cast）末尾にゲストが入る前提で `# 出演DJ` に persona を含める（呼び出し側で cast にゲストを足して渡すため自動）。
- `guest` 非 null のとき制約を追加（Mac s14 §4 / DialogueScriptGenerator.swift 一致）:
  - 「これはゲストを迎えるコーナー。ゲスト『{guest}』が冒頭で軽く挨拶し（番組名の名乗りや大げさな自己紹介は不要）、メイン『{main}』の進行でテーマ『{theme}』を会話する。」
  - 「ゲスト『{guest}』は『{theme}』に詳しい専門家として、具体的なエピソードや豆知識を交えて語る。サブは相づち・質問で会話を回す。」
  - 「コーナーの最後に、メインがゲスト『{guest}』へお礼を述べてから曲を紹介する。」
- 正式紹介はリード文が担うため台本では繰り返さない。greeting 分岐（W13.5）とは独立（ゲストコーナーは greeting=null・guest 非 null）。

---

## 5. 配線（`AIRadio.Core/BroadcastEngine.cs`, App）

- `BroadcastEngine.RunAsync` に `IReadOnlyList<DjProfile>? guests = null` を追加（既定 空）。シグネチャは
  `RunAsync(plan, themes, corners, djs, BroadcastControl? control = null, IReadOnlyList<DjProfile>? guests = null, CancellationToken ct = default)`
  （control の後・ct の前。既存の positional `control` 呼び出しを壊さない）。
- `BroadcastEngine` ctor に `Func<int, int>? randomIndex = null`（既定 `Random.Shared.Next`）を**末尾（timeZone の後）に追加**（CornerEngine と同形。ゲスト選定用。既存 ctor 呼び出し〔tests の positional `onEvent, timeZone` / composition の named〕を壊さない）。
- **preflight にゲスト検証＋選定**（`GuestCornerId` 設定 かつ `plan.IncludesGuestCorner` のときのみ。Mac 一致）。**配置は既存の anchor/talk/letter/news preflight の後、weekly_cast の cast 解決の前**（設定不正を cast 解決より先に弾く、Mac 順）:
  - guest コーナー template が corners に実在（不在は fail-fast）。
  - `GuestCornerId` が talk/letter corner と重複しない（重複は fail-fast）。
  - guests プールが空でない（空は fail-fast）。
  - guests にレギュラー（djs）と衝突する id が無い（衝突は fail-fast）。
  - `selectedGuest = guests[randomIndex(guests.Count)]`。
  - `IncludesGuestCorner` でない（N<2 等）ならプール空でも fail-fast しない（誤って中止しない、Mac §3）。
- **`selectedGuest` の引き回し**: `RunAsync` → `BroadcastAsync`（`DjProfile? guest` 引数を追加）→ ローカル `EnsurePrepared` → `StartPreparation`（`DjProfile? guest` 引数を追加）。`guestCornerId` は `plan.Blueprint.GuestCornerId` から参照。
- talk 準備の `CornerContext` 構築（`StartPreparation`）。**判定順が重要**: まず `segment.CornerId == plan.Blueprint.GuestCornerId` を判定し、真なら
  `new CornerContext(CastDjIds: castDjIds, Greeting: null, LeadIn: corner.LeadIn, Guest: guest)`（**ゲスト分岐は `index == firstTalkIndex` の greeting 分岐より前**に評価する。万一ゲストセグメントが最初の talk になっても誤って挨拶を付けないため。Mac 一致）。
  それ以外の talk は従来どおり（最初の talk＝greeting / 他＝corner.LeadIn）。
- App `BroadcastComposition`: `guests.yaml` を読み（**ファイルが無ければ空**＝ゲスト無効時はそれでよい。あるのに壊れていれば fail-fast）、`engine.RunAsync(plan, themes, corners, djs, control, guests, ct)` に渡す。
- `DemoRunner` の `corner` デモは無改修（guest 既定 null）。`MenuBar`/`TrayController` 変更なし。

---

## 6. config

### `config/guests.yaml`（新規）
§1 の 10 名（id/name/speaker_id/persona）。

### `config/corners.yaml`（guest コーナー追加）
```yaml
- id: guest
  title: "ゲストコーナー"
  theme: "最近ちょっと気になっていること"
  themes: [ ...free_talk と同じプール... ]
  format: guest
  lead_in: "{ampm}{hour}時{minute}分になりました。次はゲストコーナーです。本日は{guest}さんを迎えて、{theme}について熱く語ってもらいます。"
  dj_ids: [zundamon, metan]   # cast は当日編成で上書きされるが必須項目として置く
  fallback_track_uri: "spotify:track:..."
```

### `config/program.yaml`（guest 追加）
```yaml
  guest:
    corner_id: guest
```

---

## 7. 影響ファイル一覧

| 層 | ファイル | 変更 |
|---|---|---|
| Core | `Program.cs` | `ProgramBlueprint.GuestCornerId`、`ProgramPlan` ゲスト挿入（`IncludesGuestCorner`/`BodySegment`/`PatternBodySegment`/`TotalSegmentCount`）|
| Core | `Dialogue.cs` | `CornerContext.Guest` 追加 |
| Core | `CornerEngine.cs` | `CornerFormat.Guest` 実処理、`PreparedCorner.Guest`、`CornerEvent.GuestReady`、lead_in の `{guest}`/`{theme}` 置換、run で guest を cast 末尾へ |
| Core | `DialogueScriptGenerator.cs` | `guest` 引数 + ゲストフレーミング制約 |
| Core | `BroadcastEngine.cs` | `guests` 引数、`randomIndex` ctor、ゲスト検証＋選定、guest セグメントの CornerContext.Guest |
| Infra | `Config/GuestsConfig.cs` | 新規（`guests.yaml` → `IReadOnlyList<DjProfile>`）|
| Infra | `Config/ProgramConfig.cs` | `ProgramDto` に **`Guest` (TalkDto) のみ追加** → `ProgramBlueprint.GuestCornerId`。**`artist_feature` は引き続き未追加＝IgnoreUnmatchedProperties で無視（W15）**|
| App | `Composition/BroadcastComposition.cs` | `guests.yaml` ロード（任意）・engine へ渡す |
| config | `guests.yaml`（新規）/ `corners.yaml` / `program.yaml` | プール・guest コーナー・guest.corner_id |
| docs | `design.md` | W14 行更新 |
| Core.Tests | `ProgramPlanTests.cs` | ゲスト挿入（N=2/4/エンドレス・最初の news 直後 1 回・2 回目なし・N≤1 なし・total +1・N に数えない）|
| Core.Tests | `BroadcastEngineTests.cs` | ゲスト選定（決定論）・context.Guest 伝播・fail-fast（プール空/衝突/template 不在/重複）・1 放送 1 回 |
| Core.Tests | `CornerEngineTests.cs` / `DialogueScriptGeneratorTests.cs` | guest format（cast 末尾・lead_in 置換・専門家フレーミング）|
| Infra.Tests | `GuestsConfigTests.cs`（新規 or 既存に追加）| 読み込み・必須欠落 |
| Infra.Tests | `ProgramThemesConfigTests.cs` | `guest.corner_id` → GuestCornerId 読み込み（正例）。**既存 `Program_IgnoresFutureSliceKeys` を artist_feature のみに絞る**（guest はもう無視されない）|

---

## 8. テスト（`dotnet test AIRadio.slnx` 緑が完了条件）

- **`ProgramPlan`（ゲスト挿入）**: GuestCornerId 設定で N=2/4/エンドレスに最初の news 直後 1 個だけ guest（cornerId=GuestCornerId）、2 回目 news 後は無し、N≤1（news なし）は無し、`TotalSegmentCount` +1、free_talk 本数（N）は不変。GuestCornerId=null では挿入なし（W13/W13.5 の plan 不変）。
- **`GuestsConfig`**: 読み込み（id/name/speaker_id/persona）、`guests` 空・id/name/speaker_id 欠落 fail-fast。
- **`ProgramConfig`**: `guest.corner_id` 読み込み（省略は null）。
- **`DialogueScriptGenerator`**: guest 非 null で「冒頭挨拶・メイン進行・専門家・お礼」制約 + ゲスト persona、null で従来どおり（混入なし）。
- **`CornerEngine`（guest）**: cast 末尾にゲスト、lead_in の `{guest}`/`{theme}` 置換（時刻は run 時展開）、台本プロンプトに専門家フレーミング、`PreparedCorner.Guest` 格納、run で guest 行がゲスト speaker、締め曲・pause。`CornerEvent.GuestReady`。
- **`BroadcastEngine`**: プールから決定論選定（randomIndex 注入）、guest セグメント準備に `context.Guest` が渡る、fail-fast（プール空 / レギュラー衝突 / template 不在 / talk・letter と id 重複）、`IncludesGuestCorner` でないときはプール空でも中止しない、1 放送ちょうど 1 回。
- **既存保証**（完全静寂・ローリング窓上限・ED で終了・時報リード文の時刻正確性・W13.5 曜日替わり）は不変。

---

## 9. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。エラーコード追加なし。
- **実機確認（要 VOICEVOX + Spotify Premium）**:
  - 最初のニュース直後に **1 回だけ**ゲストコーナー。「{時刻}になりました。次はゲストコーナーです。本日は〈ゲスト〉さんを迎えて、〈テーマ〉について熱く語ってもらいます」→ 全員会話（ゲストは専門家役）→ お礼 → 曲。
  - ゲストはレギュラー以外から選ばれ、声（speaker）が切り替わる。あんこもんは語尾「もん」。
  - 2 回目以降のニュース後にはゲストが入らない。

---

## 10. 確定設計判断（W14）

1. **ゲスト挿入は `ProgramPlan` の固定位置（最初の news 直後 = body 4）**（Mac 同様。ランダム枠選択は廃止）。artistFeature 挿入は W15（本スライスでは `IncludesGuestCorner`/`BodySegment` のみ、`insertionsBefore` は guest 1 種）。
2. **ゲスト選定の乱数 = `Func<int,int>`**（BroadcastEngine ctor。新 `IRandom` は作らない＝§3-5、CornerEngine W12 と同形）。テストは決定論注入。
3. **ゲストは `djs` と別管理**（`guests.yaml` プール）。cast には run/prepare で末尾に補い、`PreparedCorner.Guest` で持ち回す（djs に居ないため）。
4. **lead_in の `{guest}`/`{theme}` は準備時置換、時刻は発話直前**（W13.5 の lead_in 機構に置換ステップを足す。全コーナー共通で無害）。
5. **`RunAsync` の guests は control の後・ct の前の optional**（既存 positional 呼び出し非破壊）。`guests.yaml` は任意ファイル（無ければ空＝ゲスト無効）。

Mac リファレンス: `Program.swift`（guestCornerId/includesGuestCorner/bodySegment）/ `BroadcastEngine.swift`（guest 検証・選定・cornerContext）/ `CornerEngine.swift`（guest format・lead_in 置換）/ `DialogueScriptGenerator.swift`（guest）/ `GuestsConfig.swift` / `ProgramConfig.swift`。
