# W16 — DJ の今日の気分 ＋ 常設コーナーのテーマ宣言リード文

Mac 版 `s16`（`../AIRadio_Mac-main/docs/specs/s16-mood-and-theme-leadin.md`）の Windows 移植。
標準フォーマット13項目のうち未実装だった **③「DJ の今日の気分」** を採用し、あわせて**常設コーナー（フリートーク）の
リード文をテーマ宣言化**する小スライス。どちらも W13.5 の「冒頭コーナー＝挨拶ダイアログ／途中コーナー＝時報リード文」の
二層構造に**素直に乗る**ため、変更は最小（**プロンプト 2 行追加 ＋ config 1 行変更**）。

> 進め方は確立パターン（CLAUDE.md §7）: 本 spec 凍結 → 仕様検証 Workflow → ユーザー承認 → 実装 → 多角レビュー →
> `dotnet test AIRadio.slnx` 緑 → commit/push → Confluence ミラー。
> ※ file:line は 2026-06-18 時点（W15 commit `460f3b1`/`27f6d80` 後）のコード地図に基づく指標。実装時に現物で再確認する。
> ※ **挙動の正は Mac の shipped コード**（`DialogueScriptGenerator.swift`）。s16 prose に無い post-spec ライブ修正は §3-2 で明示し、ユーザー承認に委ねる。

---

## 0. スコープ

### 入れる（Mac s16 §3〜§4 ＋ Mac shipped コード）
1. **① DJ の今日の気分**: 冒頭コーナー（`greeting` 非 null）の台本プロンプトに「出演者紹介のあと、メインが『今日の気分』を一言添えてから本題へ橋渡しする」制約を追加（§3-1）。
2. **①' 時刻・時間帯の断定抑制（＝memory「深夜抑制」の W16 側）**: 同じ greeting 分岐に「挨拶は挨拶語にとどめ、『深夜』『夕方』『◯時』など具体的な時刻・時間帯は断定しない」制約を追加（§3-2）。**s16 prose には無く Mac shipped コードにのみ存在する post-spec ライブ修正**（「s16/s17 ライブで『深夜』誤発言を確認」）。気分を語らせると時間帯を口走りやすいため ① と同じ場所・同じ理由で対になる。**ユーザー承認で採否を確定**。
3. **② テーマ宣言リード文**: `config/corners.yaml` の free_talk `lead_in` を「ここからはフリートークのコーナーです」→「ここからは{theme}について話そうと思います」に変更（§4）。**コード変更ゼロ**（`{theme}` 置換は W14 で全コーナー lead_in に適用済み）。

### 入れない（後続スライス／不採用）
- **DailyContext 拡張（曜日名・記念日の軽重）** → **W17**（Mac s17）。本スライスの気分は **季節・日付（`SeasonPhrases.DateContext`）ベース**のまま。
- **journalContext（長期記憶の冒頭一言）** → **W18**（Mac s18）。Mac shipped の greeting 分岐は `journalContext` 非空時に一言添えるが、Win は `DialogueScriptGenerator` にまだ `journalContext` 引数が無い（W18 で追加）。本スライスでは**持ち込まない**。
- letter / guest / artist_feature の lead_in 変更（②の対象は free_talk のみ）。

> **既存下地（W13.5/W14 で完成・編集不要）**: greeting 分岐（`DialogueScriptGenerator.cs:75-78`）、lead_in の `{theme}`/`{guest}` 準備時置換（`CornerEngine.cs:166-174`、**全コーナー lead_in に無条件適用**）、free_talk の lead_in（`corners.yaml:24`）。本スライスは既存機構に文言を足すだけで新規型・新規 interface・新規 config キーを一切作らない（§3-5）。

---

## 1. 配置（標準13項目との対応）

| 項目 | 現状（W15 まで） | 本スライス後 |
|---|---|---|
| ③ DJ の今日の気分 | 冒頭コーナーは 挨拶＋番組名＋出演者紹介 のみ | 上記に続けて**今日の気分を一言**→本題（§3-1）|
| （深夜抑制 W16 側）| 冒頭で時刻・時間帯を断定しうる | 挨拶語にとどめ時刻・時間帯を断定しない（§3-2）|
| ④/⑧ 常設コーナー（free_talk）| リード文「…フリートークのコーナーです」 | リード文「…〈テーマ〉について話そうと思います」（§4）|

- 冒頭コーナー（`firstTalkIndex` の talk）は `greeting` 非 null・`LeadIn` を使わない（W13.5。`BroadcastEngine.StartPreparation` の greeting 分岐）。2 本目以降の常設トークは `greeting=null`・`corner.LeadIn` を使う。
- よって **① と ①' は冒頭コーナーのみ、② は冒頭以外の常設 free_talk トークに効く**（重複しない）。letter/guest/artist_feature は別建てで②の対象外。

---

## 2. 該当コードと既存挙動の確認（実装前検証済み）

- `DialogueScriptGenerator.MakeRequest`（`DialogueScriptGenerator.cs:37-100`）の `if (greeting is not null)` ブロック（`:75-78`）が冒頭コーナーの制約を積む。現状は intro 1 行のみ。**ここに ①・①' の 2 行を追加**する。
- `else` ブロック（`:79-82`、途中コーナー）は **W12 の番組終了風抑止文（「本日最後の曲/ラストナンバー/また来週/お別れの時間」をしない）を含む Win 先行版**。Mac shipped の else（`DialogueScriptGenerator.swift:120`）より長いが、これは意図的な Win 改善（memory 既述）。**本スライスでは触らない**（regression 保護）。
- `CornerEngine.PrepareAsync`（`CornerEngine.cs:166-174`）は `context.LeadIn` 非空時に `leadIn.Replace("{theme}", theme)` を**無条件適用**（その後 guest 非 null なら `{guest}` も置換）。時刻プレースホルダ（`{ampm}`/`{hour}`/`{minute}` 等）は残し、発話直前展開（W13.5 §5）。**②は free_talk の lead_in に `{theme}` を入れるだけでこの機構に自動的に乗る**。

---

## 3. ① DJ の今日の気分（冒頭コーナーの greeting プロンプト拡張）

### 3-1. 気分の一言（s16 ①・Mac shipped `DialogueScriptGenerator.swift:111`）

- `DialogueScriptGenerator.cs` の `if (greeting is not null)` ブロック内、既存 intro 行（`:77`）の**直後**に追加:
  ```csharp
  constraints.Add($"出演者紹介のあと、メイン「{main}」が『今日の気分』を一言（調子・気分や、今日の天気・季節の感想など）添えてから、本題へ自然に橋渡しする。");
  ```
- `dateContext`（季節・日付。`SeasonPhrases.DateContext`）は既にプロンプトの「# 今日の日付と季節」節に注入済み（`:61-64`）＋「季節や時候の話は、上の日付・季節に合わせる」制約（`:95-98`）があるため、気分はそれに整合した自然な一言になる。
- **config 化しない**: greeting と同じくプロンプト駆動（W13.5 踏襲）。プロンプト外部化（`prompts.yaml`）は将来まとめて。

### 3-2. 時刻・時間帯の断定抑制（①'・Mac shipped `DialogueScriptGenerator.swift:114`・post-spec ライブ修正）

- 同ブロックの 3-1 行の**直後**に追加:
  ```csharp
  constraints.Add($"挨拶は「{greeting}」のような挨拶語にとどめ、『深夜』『夕方』『◯時』など具体的な時刻・時間帯は断定しない（正確な時刻はこの場面では言わない）。");
  ```
- **理由**: 冒頭挨拶の場面では正確な時刻を読まない（時報リード文が他コーナーで発話直前に読む。W13.5 §5）。気分・天気を語らせると LLM が「深夜だけど〜」等と時間帯を口走り、実時刻とズレる事故が Mac ライブで観測された。① と同じ greeting 分岐に同居する対の制約。
- **Mac での所属**: コメントは「s16/s17 ライブで確認」だが、**コード上は greeting 分岐（s16 が編集する場所）にあり**、memory の整理でも「深夜抑制=W16,W17」と W16 を含む。greeting プロンプト側の断定抑制は W16、DailyContext 由来の時間帯素材の扱いは W17。
- **ユーザー承認ポイント**: s16 spec prose に無い追加。①と一体で入れる（推奨）か、W17 まで保留するかをユーザーが判断。Win は実機確認を最後に一括するため、ここで入れておくと「深夜」誤発言を最終ライブ前に予防できる。

- 出力契約・行数下限（4 行下限）・語尾混線禁止などの既存制約は不変。greeting 分岐の追加は**制約文 2 本のみ**（型・シグネチャ変更なし）。

---

## 4. ② テーマ宣言リード文（config のみ・コード変更なし）

- **変更は `config/corners.yaml` の free_talk `lead_in` 1 行だけ**:
  - 現状（`corners.yaml:24`）: `"{ampm}{hour}時{minute}分になりました。ここからはフリートークのコーナーです。"`
  - 変更後: `"{ampm}{hour}時{minute}分になりました。ここからは{theme}について話そうと思います。"`
- `{theme}` は free_talk の `themes:` プールから準備時にランダム選択された値（`CornerEngine.SelectTheme`）で、`CornerEngine.PrepareAsync` が準備時に置換（§2）。例: 「…ここからは旅行・おでかけについて話そうと思います。」
- **letter / guest / artist_feature の lead_in は対象外**（建付けが別。guest/artist は既に `{guest}`/`{artist}` 入り）。
- 時刻プレースホルダのみ発話直前展開される点は従来どおり（時報として正確）。

---

## 5. 影響ファイル一覧

| 層 | ファイル | 変更 |
|---|---|---|
| Core | `DialogueScriptGenerator.cs` | greeting 分岐に制約 2 行追加（① 気分 ＋ ①' 時刻断定抑制）。シグネチャ・型は不変 |
| config | `corners.yaml` | free_talk `lead_in` を `{theme}` 宣言形に変更（1 行）|
| docs | `design.md` | W16 行を「実装済み」に（必要なら注記）|
| Core.Tests | `DialogueScriptGeneratorTests.cs` | greeting 非 null で気分・時刻断定抑制の制約が含まれる／greeting null で含まれない（regression: 途中コーナーの W12 抑止文は不変）|
| Core.Tests | `CornerEngineTests.cs` | free_talk の lead_in に `{theme}` → `PreparedCorner.LeadIn` が選択テーマで置換・時刻プレースホルダは残る（既存の置換テストに free_talk ケースを追加）|
| Infra.Tests | `DjsCornersConfigTests.cs` 等（任意）| free_talk `lead_in` に `{theme}` が含まれることを設定テストで担保（任意・薄く）|

- **新規ファイル・新規型・新規 interface・新規 config キーはゼロ。** エラーコード追加なし。

---

## 6. テスト（`dotnet test AIRadio.slnx` 緑が完了条件）

- **①/①'（`DialogueScriptGeneratorTests`）**:
  - `greeting` 非 null（冒頭）: プロンプトに「今日の気分」指示（3-1 の文言断片）と「時刻・時間帯を断定しない」指示（3-2 の断片、例「具体的な時刻・時間帯は断定しない」）が**両方含まれる**。
  - `greeting` null（途中）: 上記 2 指示は**含まれない**。かつ W12 の番組終了風抑止文（「ラストナンバー」等）は**従来どおり含まれる**（regression）。
  - guest / letter / 季節制約など既存分岐は不変（混入なし）。
- **②（`CornerEngineTests`）**: free_talk（format=free_talk）の `lead_in` に `{theme}` を含むテンプレートで `PrepareAsync` → `PreparedCorner.LeadIn` が**選択テーマ文字列で置換**され、時刻プレースホルダ（`{hour}` 等）は**残る**。`themes:` プール固定 or `randomIndex` 注入で決定論。
- **設定（任意）**: `corners.yaml` の free_talk `lead_in` に `{theme}` が含まれる（薄い回帰ガード）。
- **回帰（不変）**: 完全静寂・ローリング準備・ED で終了・時報リード文の時刻正確性・W13.5 曜日替わり・W14 ゲスト・W15 特集。

---

## 7. 受け入れ条件

- `dotnet build` / `dotnet test AIRadio.slnx` 全グリーン・0 警告。エラーコード追加なし。新規型・interface・config キーなし。
- **実機確認（要 VOICEVOX + Spotify Premium。全実装完了後に一括）**:
  1. 番組冒頭のトークで、メインが挨拶・番組名・出演者紹介のあと**「今日の気分」を一言**述べてから本題に入る。
  2. 冒頭の挨拶で「深夜」「◯時」など具体的な時刻・時間帯を**断定しない**（挨拶語にとどまる）。
  3. 2 本目以降の常設トークのリード文が「**◯時◯分になりました。ここからは〈テーマ〉について話そうと思います。**」になり、毎回同じ「フリートークのコーナーです」案内が消える。
  4. 既存の進行（曲・ニュース・お便り・ゲスト・特集・完全静寂）に影響なし。

---

## 8. 確定設計判断（W16）／ Win 固有 parity 注記

1. **新規型・interface・config キーを作らない**（§3-5）。greeting 分岐に制約文 2 本＋corners.yaml 1 行のみ。
2. **挙動の正は Mac shipped コード**。s16 prose に無い「時刻・時間帯断定抑制」（①'）を移植する（post-spec ライブ修正＝W11/W15 で確立した「prose≠code→code 準拠」原則）。**ユーザー承認で採否確定**。
3. **journalContext（s18）は持ち込まない**＝Win `DialogueScriptGenerator` に `journalContext` 引数を足さない（W18）。気分は **`SeasonPhrases.DateContext` ベース**（曜日名・記念日は W17）。
4. **`else`（途中コーナー）分岐は不変**。Win は W12 の番組終了風抑止文を持つ意図的先行版で、Mac shipped の短い else に**戻さない**（regression 保護）。
5. **②はコード変更ゼロ**。`{theme}` 置換は W14 で全コーナー lead_in に無条件適用済み（`CornerEngine.cs:169`）。free_talk の lead_in に `{theme}` を入れるだけ。

Mac リファレンス: `DialogueScriptGenerator.swift:108-121`（greeting 分岐＝① 気分行 `:111` ／ ①' 時刻断定抑制行 `:114` ／ journalContext `:116-118`＝W18）/ `CornerEngine.swift:166-171`（leadIn 埋め込みブロック・`{theme}` 置換実行は `:168`）/ `config/corners.yaml:12`（free_talk lead_in ＝変更後文言と一字一句一致）。SoT: `docs/specs/s16-mood-and-theme-leadin.md`。
