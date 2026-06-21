# W-DEDUP — 選曲の重複回避（演奏済みリング）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。**追加機能スライス＝Mac リファレンスなし**（Mac 版にも選曲重複回避は無い）。
> ユーザーフィードバック（2026-06-21）: 同じ曲が**毎放送ごと**・**同一放送内で 2 度**選ばれるのが耳につく。直近に流した曲を記録し、選曲時に避けたい。
> 設計 Q&A（2026-06-21）で確定:
> - 照合キー＝**Spotify trackURI のみ**（今回）。「曲名+アーティスト正規化キー」併用は**将来の余地として残す**（title/artist は記録するが今回の判定には使わない）。同曲別 URI（シングル/アルバム）やカバーがどう選ばれるかを観察してから判断する。
> - 事前一括選曲（メニュー「本日の歌の準備」）は**不採用**。既存のローリング先読み（前曲の再生中に裏で次を選曲）で追加の検索遅延は無音化しないため。
> - 仕様検証 Workflow（4 次元・2026-06-21）で確定 9 件を反映（CONC-1 アトミック予約／WIRE-1,2 config/DTO／OBS-2,3,4,5,7 テスト・観察可能性／EDGE-1 観察ガード）。

---

## 1. 概要・スコープ

直近に再生した曲を `PlayedSongHistory`（演奏済みリングバッファ・最大 N=100・FIFO）に記録し、`SongPicker` の候補選定で「**再生可能 かつ 直近に流していない**」を満たす最初の曲を採る。選曲は既存のローリング先読み（前の曲の再生中に裏で次を準備）に乗るため、追加の検索遅延は放送中の無音にならない。

**含むもの**:
1. Core 新規 `PlayedSongHistory`（純粋・スレッドセーフ・URI リング N・`IsRecent(uri)`〔読取〕／`TryReserve(track)`〔原子的判定＋予約〕）。
2. `SongPicker.PickAsync` に任意 `PlayedSongHistory?`（末尾 optional・null なら従来挙動）。候補ループで `TryReserve` が成功した最初の再生可能曲を採用。
3. 配線: `BroadcastComposition` が**アプリ起動中ただ 1 つ**の履歴を生成（放送をまたいで保持＝毎放送の重複も回避）→ `BroadcastEngine`（冒頭曲 `_songPicker`）と `CornerEngine`（締め曲）へ注入。
4. config: `program.yaml` に履歴サイズ（既定 100・0 で無効）。
5. 観察可能性: 採用曲ログに trackURI を併記（URI のみで dedup する判断を実運用で観察するため＝OBS-4）。
6. テスト。

**スコープ外**:
- **アーティスト特集（W15）**: `IArtistCatalog` / top-tracks の別経路ゆえ履歴の読み書き対象外（特集↔通常の重複は許容）。特集内の重複排除は既存どおり `ArtistFeatureEngine` 側。
- **「曲名+アーティスト正規化キー」による同曲別 URI / カバーの排除**: title/artist は履歴に記録して**将来の余地を残す**が、今回の判定には使わない（URI のみ）。シングル/アルバム別バージョンやカバーの出方を観察してから判断する。
- **ディスク永続化**（再起動跨ぎの重複回避）: 今回は in-memory・アプリ終了でクリア。将来 `%LOCALAPPDATA%`（journal / program-length 前例）で追加可。
- **フォールバック曲（`fallback_track_uri`）の重複回避**: 安全網ゆえ履歴で弾かない・予約もしない（全候補衝突時の最後の砦）。
- **全候補衝突時の追加 LLM ラウンド / 「避けて」ヒント注入**: 今回は不採用（観察後に偏りが見えたら追加）。
- **同候補（同一クエリ）内の別バージョン探索**: 今回はしない（候補ごとの先頭再生可能曲のみ判定）。

**不変条件**: `SongPicker` の既存呼び出し（履歴 null）・`ITrackSearcher` / `ILLMBackend` シグネチャ・各エンジンの既存挙動は無改修で緑（後方互換）。履歴未注入（CLI demo 等）では従来どおり。

---

## 2. 確定設計判断

1. **照合キー＝Spotify trackURI（完全一致）**。`SongPicker` は検索ヒット曲の `TrackInfo.Uri` を持つので URI 完全一致で判定。部分一致・正規化はしない（誤除外回避・観察優先）。
2. **履歴は `TrackInfo`（uri / title / artist）をそのまま積む**。判定は uri のみだが、title / artist を持つことで「将来の正規化キー併用」「避けてヒント」を**コード追加なしのデータ前提**として残す。**保持メタは first-wins**（同一 URI を別 title/artist で再投入しても初回のメタを保持・上書きしない＝OBS-5）。
3. **スコープ＝アプリ起動セッション（放送をまたいで保持）**。`BroadcastComposition` がただ 1 つ生成し、`RunAsync`（放送ごと）を跨いで同一インスタンスを使う（`TrayController` が composition を 1 度だけ生成）。これにより「**毎放送同じ曲**」を回避。アプリ終了で消える（in-memory）。**放送ごとにはクリアしない**（クリアすると毎放送同じ冒頭曲になり得るため）。
4. **アトミックな予約でスレッドセーフ（CONC-1・must_fix）**。ローリング先読み（窓 2・`PreparationLedger`）で複数の準備タスクが**並行に選曲**しうる。`IsRecent` 判定の後に `Record` する 2 段だと、両タスクが同時に「未既出」と判定して**同じ曲を二重採用**する TOCTOU 競合が起きる。よって**判定と記録を 1 つのロック内で行う `TryReserve(track)`**（既出なら `false`／未既出なら追加して `true`）を選曲の採否に使う。`IsRecent(uri)` は読み取り専用（テスト/観察用）として残す。`BroadcastSession` が多重放送を拒否するため `RunAsync` 自体は直列だが、その内部の先読み準備は並行しうる点が根拠。
5. **候補ループで予約（追加 LLM 呼び出しなし）**。既存の「LLM 1 コールで 5 候補 → 各候補を検索 → 先頭の再生可能曲」に対し、**先頭再生可能曲を `TryReserve`** し、成功（未既出）ならそれを採用、失敗（既出）なら次の候補へ。5 候補すべてが再生不可 or 既出なら従来どおり `fallback_track_uri` に倒す（fallback は `TryReserve` しない＝記録しない）。
6. **予約タイミング＝選定時（`PickAsync` 内・採用が確定した瞬間）**。先読みで次曲を選んだ時点で履歴に入り、直後の選曲が再選しない。停止で未再生に終わるケースは無害（次回避けるだけ）。
7. **新 interface を作らない**（§3-5）。`PlayedSongHistory` は Core 具象（純粋・テスト可）。公開面は `IsRecent` / `TryReserve` / ctor のみ（件数読み出し等の test-only API は足さない＝OBS-7）。`SongPicker` / `BroadcastEngine` / `CornerEngine` に末尾 optional で注入。
8. **新エラーコードなし**。

---

## 3. アーキテクチャ

```
BroadcastComposition（TrayController が 1 度だけ生成 → アプリ起動中保持）
  └ _songHistory: PlayedSongHistory（最初の RunAsync で program.yaml のサイズで生成・以降使い回し）
        ├→ BroadcastEngine（ctor 末尾 optional）→ _songPicker.PickAsync(..., history)   冒頭曲
        └→ CornerEngine（ctor 末尾 optional）→ new SongPicker(...).PickAsync(..., history) 締め曲
SongPicker.PickAsync:
  LLM 5 候補 → 各候補を検索 → 先頭の再生可能曲を TryReserve → 成功した最初の曲を採用 → 返す
  全滅（再生不可 or 既出のみ）→ fallback_track_uri（予約しない）
PlayedSongHistory（Core・純粋・スレッドセーフ・URI リング N・FIFO・原子的 TryReserve）
ArtistFeatureEngine（特集）は履歴に触れない＝対象外
```

### 3-1. Core（新規 `PlayedSongHistory.cs`）

```csharp
/// <summary>直近に再生した曲の演奏済みリング（最大 Capacity・FIFO・スレッドセーフ）。
/// 判定は trackURI 完全一致（W-DEDUP）。title/artist も保持し将来の正規化キー併用の余地を残す（保持メタは first-wins）。</summary>
public sealed class PlayedSongHistory
{
    private readonly int _capacity;
    private readonly LinkedList<TrackInfo> _items = new();          // 先頭=最古
    private readonly HashSet<string> _uris = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public PlayedSongHistory(int capacity = 100) => _capacity = Math.Max(0, capacity);  // 負値→0（無効）

    /// <summary>読み取り専用（テスト/観察用）。capacity 0 / 空 URI は常に false。</summary>
    public bool IsRecent(string uri)
    {
        if (_capacity == 0 || string.IsNullOrEmpty(uri)) return false;
        lock (_lock) { return _uris.Contains(uri); }
    }

    /// <summary>原子的な「判定＋予約」。既出なら false（メタは初回固定＝first-wins・上書きしない）。
    /// 未既出なら追加し（FIFO で最古破棄）true。選曲の採否はこれを使う（TOCTOU 回避＝CONC-1）。
    /// capacity 0（無効）・空 URI は判定せず true（採用可・記録しない）。</summary>
    public bool TryReserve(TrackInfo track)
    {
        if (_capacity == 0 || string.IsNullOrEmpty(track.Uri)) return true;
        lock (_lock)
        {
            if (!_uris.Add(track.Uri)) return false;        // 既出＝予約失敗（メタ上書きしない）
            _items.AddLast(track);
            if (_items.Count > _capacity)
            {
                var oldest = _items.First!.Value;            // FIFO で最古を破棄
                _items.RemoveFirst();
                _uris.Remove(oldest.Uri);
            }
            return true;
        }
    }
}
```

### 3-2. `SongPicker`（末尾 optional ＋ TryReserve 採否）

```csharp
// PickAsync 末尾に PlayedSongHistory? を追加（既存呼び出しは null = 従来挙動）
public async Task<TrackInfo> PickAsync(SongRequest request, CancellationToken ct = default, PlayedSongHistory? history = null)
// 既存の候補 foreach 内、先頭再生可能曲を取り出した後:
//   var track = results.FirstOrDefault(t => t.IsPlayable);
//   if (track is not null && (history is null || history.TryReserve(track)))
//       return track;                 // 採用（TryReserve が原子的に記録）
//   // track null（再生不可）or TryReserve=false（既出）→ 次の候補へ
// 全候補で不成立 → 従来どおり fallback（TryReserve しない＝記録されない）
```

### 3-3. `BroadcastEngine` / `CornerEngine`（履歴注入）

- `BroadcastEngine` ctor の**現末尾 optional（`banterDirectives = null`）の更に後ろ**に `PlayedSongHistory? songHistory = null`。`PickSongAsync` で `_songPicker.PickAsync(req, ct, _songHistory)`。
- `CornerEngine` ctor の**現末尾 optional（`calendar = null`）の更に後ろ**に `PlayedSongHistory? songHistory = null`。`PrepareAsync` の `picker.PickAsync(..., ct, _songHistory)`。
- 既存の positional 呼び出し（テスト含む）は真の末尾 optional ゆえ無改修で緑。`BroadcastComposition` では両エンジン生成とも **named 引数 `songHistory: _songHistory`** で渡す（positional 列に割り込ませない）。

### 3-4. `BroadcastComposition` 配線

- フィールド `private PlayedSongHistory? _songHistory;`。`RunAsync` 冒頭で blueprint ロード後に `_songHistory ??= new PlayedSongHistory(blueprint.RecentSongHistorySize)`。
- **サイズは最初の放送で確定し以降使い回す**（他の config は毎放送再読込だが、これは放送跨ぎ保持のため初回のみ参照＝サイズ変更はアプリ再起動で反映＝WIRE-1）。
- `_songHistory ??=` の遅延初期化は **`BroadcastSession` が多重放送を拒否し `RunAsync` が直列実行される**ため非ロックで安全（並行 `RunAsync` は起きない。並行するのは RunAsync 内の先読み準備で、そこは `TryReserve` のロックで保護）。
- `new BroadcastEngine(..., songHistory: _songHistory)`、`CornerEngine` 生成にも `songHistory: _songHistory` を渡す。
- `DemoRunner`（CLI）は composition を 1 回だけ new するため履歴は当該実行内のみ（従来挙動と整合）。

---

## 4. config（`program.yaml`）

```yaml
# 直近この曲数を覚えて選曲時に避ける（演奏済みリング・W-DEDUP）。0 で無効。
# アプリ起動中ずっと保持し、毎放送・同一放送内の重複を回避する（アーティスト特集は対象外）。
# 注: このサイズは最初の放送で確定し以降使い回す（他の設定と異なり放送ごとには再読込しない）。
#     変更はアプリ再起動で反映される。
recent_song_history_size: 100
```

DTO / ローダ（WIRE-2）:
- `ProgramDto.RecentSongHistorySize` は **`int?`**（YAML key 欠落 = null）。`FromYaml` で **`null → 100`**、それ以外（0・負値含む）は**そのまま** `ProgramBlueprint.RecentSongHistorySize` へ渡す。
- `ProgramBlueprint` には `WeeklyCast` と同様 **init プロパティ**で `public int RecentSongHistorySize { get; init; } = 100;` を追加（positional 引数は増やさない）。`FromYaml` の object-initializer（`{ WeeklyCast = ... }` と同位置）に `RecentSongHistorySize = ...` を足す。
- **0 = 無効**（`PlayedSongHistory` が常に採用可・記録しない）／**負値**は `PlayedSongHistory` ctor が 0 にクランプ（無効）。クランプ責務は ctor に一本化し、`ProgramConfig` は「欠落 → 既定 100」の補完だけ行う。

---

## 5. エラーコード

新エラーコードを追加しない。選曲失敗・全候補衝突は従来どおり fallback（fail-tolerant）。

---

## 6. テスト計画（xUnit・`dotnet test AIRadio.slnx` 緑＋0 警告）

- **`PlayedSongHistory`**（Core・純粋）:
  - `TryReserve` 初回 true → `IsRecent` true。再 `TryReserve`（同 URI）は false（既出）。未記録 URI は `IsRecent` false。空 URI は `TryReserve` true（記録しない）・`IsRecent` false。
  - **capacity 境界（off-by-one 固定）**: N 件 `TryReserve` では破棄ゼロ（全 URI が `IsRecent` true）／N+1 件目で最古 1 件のみ破棄（最古→false・他は true）。`> _capacity` を `>=` 誤実装と区別できるよう N と N+1 の両方をアサート。
  - **最古破棄後の同 URI 再 `TryReserve`**: 最古 URI が破棄され false になった後、再 `TryReserve` で true（最新側へ入り直り）になり、以後の破棄対象としても最新扱い（eviction の `_uris.Remove` の正しさを固定）。
  - **同一 URI・別 title/artist の再 `TryReserve`**: 件数は増えず（false で return）、保持 `TrackInfo` は初回メタのまま（first-wins・上書きしない＝§2-2 のデータ前提）。
  - **capacity=1**: 1 件 true、2 件目で 1 件目即破棄（1 件目 false・2 件目 true）。
  - **capacity=0（無効）**: `TryReserve` 常に true・`IsRecent` 常に false（記録しない）。
  - **スレッドセーフ／原子性（CONC-1）**: (1) 多数スレッドから distinct URI を並行 `TryReserve`/`IsRecent` しても例外・デッドロックなく整合（並行バースト）。(2) **同一 URI を多数スレッドが同時 `TryReserve` したとき true はちょうど 1 つ**（原子予約）。(3) 続けて既知 URI を capacity 個ぶん逐次 `TryReserve` 後、その capacity 個が `IsRecent` true・溢れた古い URI が false（上限遵守を `IsRecent` 経由で観測。件数は公開しない＝§3-5）。
- **`SongPicker.PickAsync`**（fake LLM/searcher）:
  - 先頭候補が既出 → 次候補の未既出・再生可能曲を採る（`FirstOrDefault(IsPlayable)` の後段で `TryReserve` が採否を分けることを判別）。
  - 全候補が既出 or 再生不可 → fallback（fallback は記録されない・`IsRecent` に入らない）。
  - 採用曲が記録される（次回 `IsRecent` true）。
  - 履歴 null（既定）→ 従来挙動（既存テスト無改修で緑）。
- **`BroadcastEngine` + `CornerEngine`（機構・Core テスト）**: 既定ハーネスは冒頭曲=`FirstSongTrack`／締め曲=`TalkSongTrack` と**別 URI ゆえ素の green は dedup を証明しない**。意図的に URI を衝突させた専用 fake で構成（OBS-2）:
  - **同一放送内**: 冒頭曲 `SongPicker` と `CornerEngine`（内部 `new SongPicker`）に**同一 `PlayedSongHistory`** を注入し、両者の `FakeTrackSearcher` が同じ URI_X を先頭に返すよう構成。冒頭曲が先に X を予約 → 締め曲は X が弾かれ次候補 Y を採る、を assert。
  - **放送跨ぎ（機構）**: 単一 `PlayedSongHistory` を生成し、冒頭曲 picker の `FakeTrackSearcher` が URI_A→URI_B を返すよう構成。1 回目で A 採用・予約 → 同一履歴で 2 回目を回すと A が弾かれ B が採られる（冒頭曲 URI が A→B に変わる）、を assert。
  - 注: `FakeTrackSearcher` は query を無視し結果を返すため、候補列ではなく searcher の結果順＋共有履歴で「2 番目が採られる」動線を作る。
- **`ProgramConfig`**: `recent_song_history_size` の読み（`int?`）・**欠落で既定 100**・**明示 0 はそのまま 0（無効）**・負値は通して ctor で 0 クランプ。
- 既存テスト（SongPicker / BroadcastEngine / CornerEngine / ProgramConfig）が無改修で緑。

> **App 配線の境界（OBS-3）**: 「`BroadcastComposition._songHistory ??=` を `TrayController` が単一 composition で `RunAsync` を跨いで使い回す＝放送ごとに再生成しない」配線は **App 層にしか観察可能性がなく、現状 `AIRadio.App.Tests` が無いため自動テスト対象外**。dedup **機構**（共有履歴で重複しない）は上記 Core テストで担保し、**App 配線**は実機受け入れ（§9）で確認する。

---

## 7. 実装の刻み（順序・各刻みで checkpoint）

1. **Core** `PlayedSongHistory`（`IsRecent` / `TryReserve` / FIFO / 原子性）＋テスト。
2. **`SongPicker`** 末尾 optional `PlayedSongHistory?` ＋ `TryReserve` 採否 ＋テスト。
3. **`BroadcastEngine` / `CornerEngine`** に末尾 optional 注入 ＋ 共有履歴の機構テスト（URI 衝突 fake）。
4. **Infra `ProgramConfig`** に `RecentSongHistorySize`（`int?` → init プロパティ）＋ `program.yaml`。
5. **`BroadcastComposition`** 配線（履歴 1 個生成・両エンジンへ named 渡し）＋ 採用曲ログに URI 併記（OBS-4）。
6. **`dotnet test` 緑・0 警告** → 多角レビュー Workflow → commit / push → Confluence ミラー → memory。

---

## 8. Win 固有 / 注記

| # | 項目 | 方針 |
|---|---|---|
| 1 | 重複回避 | 直近 N 曲の trackURI を覚えて避ける。Mac 版に無い追加機能。 |
| 2 | スコープ | アプリ起動中保持（放送跨ぎ）。アーティスト特集・fallback は対象外。 |
| 3 | 照合 | URI 完全一致のみ（今回）。title/artist は記録し将来の正規化キー併用の余地を残す（保持メタ first-wins）。 |
| 4 | 並行 | 先読み（窓2）で並行選曲 → 原子的 `TryReserve` で TOCTOU 回避（CONC-1）。 |
| 5 | 観察 | 採用曲ログに **URI を併記**（OBS-4）。同曲別 URI（シングル/アルバム）・カバーの出方を実運用で観察 → 偏りが見えたら正規化キーを追加。 |
| 6 | tail | 全候補衝突が頻発し fallback に倒れる回が増えるのは dedup バグでなく、`recent_song_history_size` 過大 or LLM 候補多様性不足のシグナル（capacity を下げる／温度・プロンプト見直し）。 |

### 8-1. 可観測性（OBS-4）

採用曲ログに trackURI を併記する。`BroadcastComposition` の表示整形（`Label`）を `track.Title.Length == 0 ? track.Uri : $"{track.Artist} / {track.Title} ({track.Uri})"` 相当に変更し、`SongStarted`（冒頭曲・特集・コーナー再生）/ `SongPicked`（締め曲プレフライト）の全表示で再生 URI を目視できるようにする。これにより観察フェーズで「同曲別 URI・カバー」が URI 差で判別できる。`TryReserve` で弾いた候補のログは今回不要（観察は再生 URI の重複有無で行う。将来必要なら onSkip seam を追加余地とする）。

---

## 9. 受け入れ条件

テスト（`dotnet test AIRadio.slnx` 緑・0 警告）:
- [ ] `PlayedSongHistory`: TryReserve/IsRecent・FIFO 境界（N/N+1）・最古破棄後の再投入復帰・同 URI 別メタ first-wins・capacity=1・capacity=0 無効・原子性（同 URI 同時 TryReserve で true は 1 つ）・並行バースト。
- [ ] `SongPicker`: 既出スキップ・全衝突で fallback・採用で記録・fallback 非記録・履歴 null で従来。
- [ ] `BroadcastEngine`+`CornerEngine`（機構）: 同一放送内重複なし・放送跨ぎ重複なし（共有履歴・URI 衝突 fake で dedup 発火を判別）。
- [ ] `ProgramConfig`: size 読み・欠落で既定 100・明示 0 は 0（無効）・負値は ctor で 0 クランプ。
- [ ] 既存テスト無改修で緑。

実機（VOICEVOX＋Spotify Premium・後日／観察）:
- [ ] 毎放送・同一放送内で同じ曲が（fallback 以外）繰り返らない（**App 配線＝放送跨ぎ保持**はここで確認）。
- [ ] アーティスト特集と通常コーナーの曲が重複しても許容される（特集は対象外）。
- [ ] 観察補助: 冒頭曲ログ（`SongStarted`）が「Artist / Title (uri)」形式なら通常選曲、生の trackURI のみ（Title 空）なら fallback に倒れた回。fallback による繰り返しは安全網であり機能バグではない。
- [ ] 観察: 同曲の別バージョン（別 URI）・カバーがどう選ばれるか。偏りが見えたら正規化キー併用を検討。
