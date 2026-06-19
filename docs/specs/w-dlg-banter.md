# W-DLG — 霊夢→魔理沙の掛け合い（トーク台本への掛け合い指示注入）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。Windows 固有の追加機能（W-AQT 完了後の承認済み新機能 B）＝Mac リファレンスなし。
> ユーザー設計 Q&A（2026-06-19）: ① トリガー＝**main DJ が霊夢（cast[0]=reimu）かつ cast に魔理沙がいる**とき（「霊夢が問いかけ→魔理沙が答える」の方向に一致・現状の火/木/土で発火）。② 適用範囲＝**その日の全 free_talk コーナー**（letter / guest / artist_feature は各自の枠組みを持つため対象外）。
> UI / 指示文言は仮（finalize フェーズで確定・§2-3）。LLM 生成ゆえ「ねえ魔理沙」始まりは強誘導（完全保証不可）。

---

## 1. 概要・スコープ

霊夢/魔理沙の日（火/木/土）の free_talk を「霊夢が問いかけ→魔理沙が答える」掛け合い調にする。実現のため、**掛け合い指示（banter directive）を config 外部化**し、当日キャストに応じて `DialogueScriptGenerator` のトーク台本プロンプトに注入する（**汎用機構**＝どのキャストでも掛け合いルールを config で追加できる）。

**含むもの**:
1. Core: `BanterRule` / `BanterDirectives`（純粋・`Resolve(cast)` で当日キャストに合うルールの指示文を返す）＋ `CornerContext.BanterDirective`（末尾 optional）。
2. `DialogueScriptGenerator` に任意 `banterDirective`（末尾 optional・空なら無注入）。非空なら constraints に 1 行追加（W12〜W18 と同じ条件付き注入パターン）。
3. `BroadcastEngine`: ctor 末尾 optional `BanterDirectives?`。当日 cast から指示文を解決し、free_talk コーナーの `CornerContext.BanterDirective` に載せる。
4. Infra: `BanterConfig`（`config/banter.yaml` ローダ・tolerant）。
5. config: `config/banter.yaml`（reimu→marisa の指示文・仮）。
6. テスト（Resolve・generator 注入・engine 配線・loader）。

**スコープ外**:
- 掛け合いの双方向化・キャスト別の細かな会話スタイル多様化（汎用 config 構造は用意するが、本スライスの配置は reimu→marisa の 1 ルールのみ）。
- letter / guest / artist_feature コーナーへの掛け合い注入（free_talk のみ）。
- 指示文言の確定（finalize）。

**不変条件**: `ILLMBackend` / `DialogueScriptGenerator` の既存呼び出し（W12〜W18 の positional）・`CornerEngine` / `BroadcastEngine` の既存挙動は無改修で緑（後方互換）。掛け合い無効（banter.yaml なし・非トリガー日）のときは従来どおりのトーク台本。

---

## 2. 確定設計判断

1. **指示文は config 外部化**（CLAUDE.md §4-1・日本語をロジックに書かない）。新規 `config/banter.yaml` に「掛け合いルール」のリスト。各ルール＝`main`（DJ id）＋`requires`（cast に必須の相手 DJ id・任意）＋`directive`（指示文）。
2. **トリガー＝`Resolve(cast)`**（Core 純粋）。`cast[0].Id == rule.Main` かつ `rule.Requires` がすべて cast に在席する最初のルールの `directive` を返す（無ければ null）。reimu→marisa は `{ main: reimu, requires: [marisa], directive: "…" }` の 1 エントリ。
3. **適用範囲＝free_talk のみ**。`BroadcastEngine` が当日 cast から指示文を 1 回解決し、StartPreparation で **`corner.Format == FreeTalk` のトーク**にだけ `CornerContext.BanterDirective` を載せる（冒頭・途中の free_talk 双方）。letter / guest / artist_feature には載せない。
4. **注入は末尾 optional ＋ 条件付き constraint**（W12 letter / W14 guest / W18 journalContext と同一パターン）。`DialogueScriptGenerator.MakeRequest` に `string banterDirective = ""` を末尾追加し、`if (!string.IsNullOrEmpty(banterDirective)) constraints.Add(banterDirective);`。**指示文は config の文字列をそのまま 1 constraint として追加**（ハードコードの和文ラッパを足さない）。
5. **fail-soft（掛け合いは非 critical 演出）**。banter.yaml 不在 → `BanterDirectives.Empty`（注入なし・従来挙動）。ルールが参照する DJ id が当日 cast にいなければ単に発火しない（preflight しない＝OP/ED script の fail-fast とは異なり、トーク演出ゆえ握り潰し）。指示文が空のエントリ・main 欠落は loader で fail-fast（malformed config）。
6. **新 interface を作らない**（§3-5）。`BanterDirectives` は Core 具象（純粋ロジック）。loader は `YamlConfigLoader + DTO + map`。`BroadcastEngine` ctor に末尾 optional 追加（journalStore 前例）。
7. **新エラーコードなし**。banter.yaml の malformed（main / directive 欠落）は `E-CFG-MISSING-FIELD-001` 再利用。

---

## 3. アーキテクチャ

```
config/banter.yaml ──(BanterConfig: Infra)──> BanterDirectives（Core・ルール集）
BroadcastEngine.RunAsync: 当日 cast 解決（既存 line 217）
  → BroadcastAsync: banterDirective = (banterDirectives ?? Empty).Resolve(cast) ?? ""   ← 1 回だけ
  → StartPreparation(Talk): corner.Format==FreeTalk のとき CornerContext.BanterDirective = banterDirective
  → CornerEngine.PrepareAsync → generator.GenerateAsync(..., banterDirective: context.BanterDirective ?? "")
       → DialogueScriptGenerator.MakeRequest: 非空なら constraints に 1 行追加 → LLM プロンプト
```

### 3-1. Core（`Dialogue.cs`）

```csharp
// 掛け合いルール（YAML 由来）。Main が cast[0]、Requires が全員 cast にいると Directive を注入。
public sealed record BanterRule(string Main, IReadOnlyList<string> Requires, string Directive);

// 掛け合いルール集（純粋・テスト可）。
public sealed class BanterDirectives
{
    public static BanterDirectives Empty { get; } = new(Array.Empty<BanterRule>());
    public BanterDirectives(IReadOnlyList<BanterRule> rules) { /* … */ }
    // cast[0]=main。main 一致かつ Requires 全員在席の最初のルールの Directive。無ければ null。
    public string? Resolve(IReadOnlyList<DjProfile> cast);
}

// CornerContext に末尾 optional 追加（W13.5/W18 と同じ context 伝播）。
public sealed record CornerContext(
    IReadOnlyList<string>? CastDjIds = null,
    string? Greeting = null,
    string? LeadIn = null,
    DjProfile? Guest = null,
    string? JournalContext = null,
    string? BanterDirective = null);   // ← 追加
```

### 3-2. `DialogueScriptGenerator`

```csharp
// GenerateAsync / MakeRequest 末尾に banterDirective を追加（journalContext の後・temperature/ct の前）。
public static LLMRequest MakeRequest(
    CornerTemplate corner, IReadOnlyList<DjProfile> djs, TrackInfo song, string? theme = null,
    string dateContext = "", ListenerLetter? letter = null, string? greeting = null, DjProfile? guest = null,
    string journalContext = "", string banterDirective = "", double temperature = 0.9)
// constraints 構築の末尾付近で:
//   if (!string.IsNullOrEmpty(banterDirective)) constraints.Add(banterDirective);
// ＝greeting 有無（冒頭/途中）に関係なく、free_talk である限り注入される（適用範囲は engine 側で free_talk に限定済み）。
```

### 3-3. `CornerEngine`

`PrepareAsync` の `generator.GenerateAsync(...)` 呼び出しに `banterDirective: context.BanterDirective ?? ""` を追加（既存引数は不変）。

### 3-4. `BroadcastEngine`

- ctor 末尾 optional `BanterDirectives? banterDirectives = null`（null → `BanterDirectives.Empty` 相当＝注入なし）。
- `BroadcastAsync` で当日 cast（`IReadOnlyList<DjProfile>`・先頭=main）を使い**一度だけ** `var banterDirective = (_banterDirectives ?? BanterDirectives.Empty).Resolve(cast) ?? "";`（greeting / journalContext と同じ箇所・同じ流儀）。
- 解決済み `banterDirective`（string）を `StartPreparation` に**引数で渡す**。StartPreparation は `castDjIds`（string 列）しか持たないため、そこで `Resolve` を呼ばない（DjProfile 不要・型不整合回避＝BANTER-002/003）。
- `StartPreparation` の Talk 分岐: `corner.Format == CornerFormat.FreeTalk` のとき `CornerContext.BanterDirective = banterDirective`、それ以外（letter）は null。guest 分岐は設定しない（null）。
- preflight 追加なし（fail-soft）。

---

## 4. config（`config/banter.yaml`）

```yaml
# 掛け合い指示（main DJ が requires の相手と組む free_talk トークの台本に注入。指示文は仮・finalize で確定）。
# main: cast の先頭（メイン）DJ id / requires: cast に必須の相手 DJ id（省略可）/ directive: LLM への指示（日本語）。
banter:
  - main: reimu
    requires: [marisa]
    directive: "霊夢が問いかけ、魔理沙が答える掛け合いを基調にする。要所で『ねえ魔理沙』と自然に話を振る。（仮）"
```

ローダ `BanterConfig`（Infra・`GuestsConfig` 形）:
- `FromYaml(string)` → `BanterDirectives`。`LoadFile(path)` は **ファイル不在 → `BanterDirectives.Empty`**（tolerant・banter は任意機能）。
- 検証順序: Deserialize → `Dto?.Banter` が null/空 → `Empty`（tolerant）。各エントリ Map: `main` 非空・`directive` 非空（欠落は `E-CFG-MISSING-FIELD-001` fail-fast）。`requires` 省略/null → 空リスト。
- DTO: `BanterDto { List<BanterEntryDto>? Banter }`、`BanterEntryDto { string? Main; List<string>? Requires; string? Directive }`。

---

## 5. エラーコード

新エラーコードを追加しない。

- banter.yaml の malformed（`main` / `directive` 欠落）＝`E-CFG-MISSING-FIELD-001`（fail-fast）。
- banter.yaml 不在＝`BanterDirectives.Empty`（注入なし・従来挙動）。
- ルールが当日 cast に合致しない／参照 DJ が不在＝単に発火しない（fail-soft・throw なし）。

---

## 6. テスト計画（xUnit・`dotnet test AIRadio.slnx` 緑＋0 警告）

- **`BanterDirectives.Resolve`**（Core・純粋）:
  - main 一致＋requires 全員在席 → directive を返す（reimu main・marisa 在席）。
  - main 不一致（marisa が main）→ null。
  - requires 不在（marisa 不在で reimu main）→ null。
  - 空 cast → null。複数ルールで最初の合致を返す。
- **`DialogueScriptGenerator.MakeRequest`**:
  - banterDirective 非空 → その文字列が `request.Prompt` に出現。
  - banterDirective 空 / 既定 → 出現しない（後方互換・既存テストが無改修で緑）。
- **`BroadcastEngine`**（fake LLM の Requests を検査）:
  - reimu main + marisa の日の free_talk トーク → 指示文がトーク台本リクエストに出現。
  - 非トリガー日（zundamon main 等）→ 出現しない。
  - letter コーナー → 出現しない（free_talk のみ）。
  - banterDirectives 未注入（ctor 省略）→ 出現しない（後方互換）。
- **`BanterConfig`**（Infra）: rule 読み（main/requires/directive）・requires 省略で空・main / directive 欠落で `E-CFG-MISSING-FIELD-001`・ファイル不在で Empty。
- 既存テスト（DialogueScriptGenerator / CornerEngine / BroadcastEngine）が無改修で緑。

---

## 7. 実装の刻み（順序・各刻みで checkpoint）

1. **Core**: `BanterRule` / `BanterDirectives`（+ `Resolve`）＋ `CornerContext.BanterDirective` ＋テスト。
2. **`DialogueScriptGenerator`**: `banterDirective` 末尾 optional ＋ constraint 注入 ＋テスト。
3. **`CornerEngine`**: `GenerateAsync` 呼び出しに `context.BanterDirective` を渡す。
4. **`BroadcastEngine`**: ctor 末尾 optional `BanterDirectives?` ＋ `Resolve(cast)` ＋ StartPreparation の free_talk 限定配線 ＋テスト。
5. **Infra `BanterConfig`** ＋ `config/banter.yaml`（reimu→marisa・仮）＋テスト。
6. **`BroadcastComposition`** 配線（banter.yaml を tolerant ロード → BroadcastEngine へ）。
7. **`dotnet test` 緑・0 警告** → 多角レビュー Workflow → commit/push → Confluence ミラー（アクセス回復後）→ memory。

---

## 8. Win 固有 注記（Mac 参照なし）

| # | 項目 | 方針 |
|---|---|---|
| W-1 | 掛け合い | Mac 版に無い Windows 固有（霊夢/魔理沙＝AquesTalk）。指示文は config 外部化。 |
| W-2 | トリガー | main=reimu かつ marisa 在席（方向性一致）。汎用ルール config で他キャストも追加可。 |
| W-3 | 範囲 | その日の全 free_talk のみ（letter/guest/artist は対象外）。 |
| W-4 | fail | 非 critical 演出＝fail-soft（banter.yaml 不在 / 非合致は無注入・preflight なし）。malformed のみ fail-fast。 |
| W-5 | 保証度 | LLM 生成ゆえ「ねえ魔理沙」始まり等は強誘導（完全保証不可）。 |

---

## 9. 受け入れ条件

テスト（`dotnet test AIRadio.slnx` 緑・0 警告）:
- [ ] `BanterDirectives.Resolve`: main/requires 合致で directive・不一致/不在/空 cast で null・複数ルール先勝ち。
- [ ] `DialogueScriptGenerator`: banterDirective 注入有/無で prompt 出現/非出現。
- [ ] `BroadcastEngine`: reimu+marisa の free_talk で注入・非トリガー日/letter/未配線で非注入。
- [ ] `BanterConfig`: rule 読み・requires 省略・malformed で fail-fast・不在で Empty。
- [ ] 既存テスト無改修で緑。

実機（VOICEVOX＋AquesTalk DLL 配置後・最後に一括）:
- [ ] 火/木/土の free_talk で霊夢が問いかけ→魔理沙が答える掛け合いになり、要所で「ねえ魔理沙」と振る（強誘導・毎回完全一致ではない）。
- [ ] 他キャストの日のトークは従来どおり（掛け合い指示なし）。
- [ ] letter / ゲスト / 特集は従来どおり（掛け合い指示なし）。

---

## 付録: 参照
- 現行 seam: `AIRadio.Core/DialogueScriptGenerator.cs`（MakeRequest constraints・末尾 optional 前例 letter/greeting/guest/journalContext）・`AIRadio.Core/CornerEngine.cs`（PrepareAsync の GenerateAsync 呼び出し・cast 解決）・`AIRadio.Core/BroadcastEngine.cs`（cast 解決 line 217・StartPreparation の Talk 分岐・CornerContext 構築）・`AIRadio.Core/Dialogue.cs`（CornerContext / CornerTemplate / CornerFormat / DjProfile）・`config/program.yaml`（weekly_cast 火木土=[reimu,marisa]）・`config/djs.yaml`（reimu/marisa）。
