# W-OP — ゆっくりOP（多声台本＋同時発話）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。**Windows 固有の追加機能スライス＝Mac リファレンスなし**（Mac 版に無い）。W-AQT 完了後の承認済み新機能 A。
> ユーザー承認（2026-06-18 方針承認＋2026-06-19 設計 Q&A 2 決定）: ① OP を多声台本（複数行・同時発話）へ拡張する方針 OK。② 同時発話＝**事前 PCM ミックス方式**。③（Q1）多声台本は**全体をダッキング BGM 上**で読む（現行 `announcement` を `script` に置換・tagline は任意）。④（Q2）同時発話のミックスは **AquesTalk 同士（同一 8kHz/16bit/mono）のみ**対応。VOICEVOX 混在（要リサンプル）は将来＝形式不一致は fail-tolerant で順次再生フォールバック＋WARN。
> UI 文言（OP セリフ・クレジット文）は**仮**（CLAUDE.md §2-3 finalize フェーズで確定）。本スライスは**機構**を実装し、配置文言は placeholder。

---

## 1. 概要・スコープ

火/木/土（霊夢メイン・魔理沙）の OP を**多声台本**にする。狙いの構成（文言は仮）:

1. 霊夢の声「ゆっくり霊夢です」
2. 魔理沙の声「ゆっくり魔理沙だぜ」（①②は順に二人の声）
3. 霊夢「ケイラボAIラジオの…（本編口上・クレジット AquesTalk＋VOICEVOX）」
4. **二人同時**「ゆっくりしていってね」（OP 締め）

これを実現するため、OP の口上を「**(話者, セリフ) の並び＋同時発話グループ**」へ一般化する（**汎用機能**＝どのキャストでも多声 OP＋同時行を使える。reimu/marisa 専用ハードコードはしない）。

**含むもの**:
1. Core ドメイン拡張: `DjSpiel` に任意 `Script`（`SpielStep` の並び）を追加。`SpielStep` は 1 声（単独発話）または複数声（同時発話グループ）。
2. Core 純粋ユーティリティ: WAV 解析/構築 `Wav` ＋ PCM 加算ミキサー `PcmWavMixer`（同一 8kHz/16bit/mono を int16 クリップ加算・短い方をゼロ詰め）。
3. `ThemeSequencer` に**多声台本オーバーロード**を追加（既存 string 版は無改修＝テスト保護）。各 step を `ITTSBackend` で合成、同時グループは各声合成→ミックス→1 WAV→既存 `IAudioPlayer.PlayAsync`。BGM ダッキング scaffold は流用。
4. `BroadcastEngine` の OP（および ED）分岐を「Script があれば多声、無ければ従来 announcement」へ分岐。speaker（DJ id）→ `DjProfile.SpeakerId` を `djs` から解決し、行ごとにプレースホルダ展開。
5. `themes.yaml` スキーマ拡張（`opening.by_dj.<dj>.script`）＋ローダ（`ThemesConfig`）。reimu の OP を script 化（placeholder 文言）。
6. テスト（ミキサー・ローダ・ThemeSequencer 多声・エンジン OP）。

**スコープ外**:
- **同時発話のクロス形式ミックス**（VOICEVOX 24kHz × AquesTalk 8kHz のリサンプル合成）。Q2 で AquesTalk 同士のみと決定。形式不一致は順次再生フォールバック＋WARN（将来 拡張）。
- B（霊夢→魔理沙の掛け合い＝トーク台本プロンプト注入）は別スライス。
- OP 文言・クレジット文の確定（finalize フェーズ）。
- BGM ダッキングの段数変更・新しい音声パイプライン（`NAudioPlayer` 無改修）。

**不変条件**: `ITTSBackend` / `IAudioPlayer` / `ISpotifyController` / `IClock` シグネチャ・`NAudioPlayer`・既存の `ThemeSequencer.RunAsync(ThemeConfig, string, int, ct)`（VOICEVOX キャストの単一 announcement OP/ED/News が使用）は**無改修**。reimu/marisa 以外のキャスト（zundamon/metan/tsumugi）の OP は従来どおり単一 announcement のまま動く（後方互換）。

---

## 2. 確定設計判断

1. **多声台本は `DjSpiel.Script`（任意・末尾 optional）**。`announcement` と `script` は排他（どちらか一方必須）。`announcement` のみ＝従来動作。`script` のみ＝多声。両方指定はローダで fail-fast（`E-CFG-MISSING-FIELD-001`）。W19b `Reading` / W-AQT 2 フィールドと同じ「末尾 optional positional で後方互換」。
2. **同時発話＝事前 PCM ミックス（Q2: AquesTalk 同士のみ）**。同一テキスト（または別テキスト）を各話者の声で `ITTSBackend.SynthesizeAsync(text, speakerId)` 合成 → **RoutingTTS が speaker_id→声種を解決**（reimu 1001→f1・marisa 1002→f2）→ 返る 8kHz/16bit/mono WAV を `PcmWavMixer` で int16 クリップ加算・ゼロ詰め → 1 WAV → `IAudioPlayer.PlayAsync`。**音声パイプライン無改修**（NAudioPlayer は WAV ヘッダを読むため 8kHz WAV をそのまま再生可）。
3. **ミキサー・WAV ヘルパは Core の純粋ロジック**（外部依存ゼロ・単体テスト可＝§3-5）。Infra でなく Core に置く理由＝`ThemeSequencer`（Core）が呼ぶため＆純粋計算ゆえ（依存方向 App→Infra→Core を侵さない）。WAV 構築の重複を避けるため `Wav.BuildPcmWav` を新設し、Infra `AquesTalk1Interop.EmptyWav()` は**任意で**同ヘルパへ寄せられる（出力バイト列を完全一致に保てば W-AQT テスト緑のまま。リスク回避のため本スライスでは EmptyWav は据え置きも可＝刻み①で判断）。
4. **形式不一致は fail-tolerant**（Q2）。同時グループの声が同一 WaveFormat（rate/channels/bits）でなければ `PcmWavMixer` は `WavFormatMismatchException`（Core・throw コードを持たない内部シグナル）を投げ、`ThemeSequencer` が catch → **そのグループの声を順次再生**にフォールバック＋**一度だけ WARN ログ**。エラーコードは増やさない（診断はログのみ）。
4'. **未定義 speaker は preflight で fail-fast**。OP/ED の script が参照する DJ id が `djs` に無ければ起動時 preflight で `E-CFG-MISSING-FIELD-001`（既存の anchor/news/weekly_cast preflight と同列）。実行時フォールバックでなく config 不正として早期に倒す（§4-3）。
5. **`ThemeSequencer` は多声オーバーロードを追加し既存 string 版は無改修**。多声版は BGM ダッキング scaffold（tagline→BGM フル→intro→duck→発話→outro→フル復帰→停止）を共有し、発話フェーズだけ「step 列を合成（同時はミックス）→再生（次 step を prefetch）」へ置換。`EmptyAnnouncement`（step 0 件）でも BGM のみ流す挙動を維持。
6. **新 interface を作らない**（§3-5）。合成は既存 `ITTSBackend`、再生は既存 `IAudioPlayer`、ミックスは Core 具象 util。`ThemeSequencer` ctor に任意 `Action<string>? warn = null` を末尾追加（後方互換）。
7. **OP のみが対象だが機構は OP/ED 共通**。多声オーバーロードの tagline フェーズは config 駆動（`theme.Tagline`/spiel tagline が非空のときだけ合成＝汎用）。ED は config 上 tagline を持たない（既存）ので no-op＝特別な ED 専用検証は設けない（CONS-9）。本スライスの配置 config は OP（reimu）だけ script 化し、ED は従来どおり announcement（tagless）。

---

## 3. アーキテクチャ

```
BroadcastEngine.PerformAsync（OP/ED 分岐）
  ├ spiel.Script なし → ThemeSequencer.RunAsync(ThemeConfig, string announcement, int speakerId, ct)  ← 既存・無改修
  └ spiel.Script あり → ResolveSpokenSteps(script, djs, main, greetings, extra)  ← DJ id→SpeakerId 解決＋行展開
                         → ThemeSequencer.RunAsync(ThemeConfig, int taglineSpeakerId, IReadOnlyList<SpokenStep>, ct)  ← 新
                              ├ 各 SpokenStep を合成（ITTSBackend・声ごと）
                              ├ Voices.Count==1 → その WAV / >1 → PcmWavMixer.Mix（不一致→順次フォールバック＋WARN）
                              └ IAudioPlayer.PlayAsync（次 step を prefetch）
PcmWavMixer / Wav（Core・純粋）: WAV 解析→int16 加算クリップ→ゼロ詰め→WAV 構築
```

### 3-1. Core ドメイン（`Program.cs`）

```csharp
// 単一の発話行（話者 DJ id ＋ セリフ。セリフは時刻等プレースホルダを含みうる＝発話直前展開）
public sealed record SpielLine(string Speaker, string Text);

// 台本の 1 ステップ：Voices.Count==1 で単独発話、>1 で同時発話（ミックス）
public sealed record SpielStep(IReadOnlyList<SpielLine> Voices);

// 既存 DjSpiel に Script を末尾 optional 追加（announcement と排他）
public sealed record DjSpiel(string Announcement, string? Tagline = null, IReadOnlyList<SpielStep>? Script = null)
{
    public bool HasScript => Script is { Count: > 0 };
}
```

`ThemedSegment` / `BroadcastThemes` は無改修（`ByDj` の値が拡張された `DjSpiel` になるだけ）。`Spiel(preferring, fallbacks)` も無改修。

### 3-2. ThemeSequencer 渡し型（Core）

エンジンが DJ id→SpeakerId を解決し、プレースホルダ展開済みにして渡す（ThemeSequencer は DjProfile を知らない＝既存の string 版同様に speakerId だけ扱う）。

```csharp
public sealed record VoiceLine(int SpeakerId, string Text);
public sealed record SpokenStep(IReadOnlyList<VoiceLine> Voices);
```

### 3-3. Wav / PcmWavMixer（Core・純粋）

```csharp
public static class Wav
{
    public sealed record Pcm(int SampleRate, int Channels, int BitsPerSample, byte[] Samples);
    // RIFF チャンクを走査して fmt と data を取り出す（オフセット 44 固定を仮定しない＝堅牢）
    public static Pcm Parse(byte[] wav);
    // 44 byte ヘッダ＋PCM を構築（AquesTalk1Interop.EmptyWav と同一レイアウト）
    public static byte[] BuildPcmWav(int sampleRate, int channels, int bitsPerSample, byte[] samples);
}

public sealed class WavFormatMismatchException : Exception { /* 形式不一致時に throw・エラーコード割り当てなし（内部シグナル） */ }

public static class PcmWavMixer
{
    // 前提: wavs.Count >= 2（単一声は ThemeSequencer 側で Mix を経由しない＝呼び出し側で担保）。
    // 全 WAV が同一 (rate, channels, bits) かつ bits==16 を要求（不一致→WavFormatMismatchException）。
    // int16 サンプルを加算し [-32768, 32767] にクリップ。短い方を 0 詰めで最長に揃える。
    public static byte[] Mix(IReadOnlyList<byte[]> wavs);
}
```

- **クリップ**: `Math.Clamp(sum, short.MinValue, short.MaxValue)`（飽和加算・歪み防止のための単純クリップ。正規化はしない＝Mac 不在の独自実装ゆえシンプルに）。
- **長さ**: 最長サンプル数に合わせ、短い WAV はゼロ（無音）詰め。
- **リトルエンディアン**: `BitConverter`（x64・LE 前提）。

### 3-4. ThemeSequencer 多声オーバーロード（Core）

```csharp
// 既存:   RunAsync(ThemeConfig theme, string announcement, int speakerId, CancellationToken ct = default)  ← 無改修
// 追加:
public async Task RunAsync(ThemeConfig theme, int taglineSpeakerId, IReadOnlyList<SpokenStep> steps, CancellationToken ct = default)
```

挙動（既存 scaffold と同形・try/catch fail-safe で `PauseIgnoringCancellationAsync(restoringVolume: theme.Volume)`）:
1. `theme.Tagline` 非空 → `_tts.SynthesizeAsync(tagline, taglineSpeakerId, ct)` → `_audio.PlayAsync`。
2. BGM `_spotify.PlayAsync(theme.TrackUri)` ＋ `SetVolumeAsync(theme.Volume)`。
3. `steps` 0 件 → 発話なしで intro/outro のみ（`EmptyAnnouncement` parity）。
4. step[0] の合成を hot-task で開始 → `_clock.DelayAsync(theme.IntroSeconds)` → `SetVolumeAsync(theme.DuckedVolume)`。
5. step 列ループ: 次 step の合成を hot-task で先行 → 現 step を再生（後述 render）→ 観測。
6. outro: `CurrentTrackDurationSecondsAsync` → 必要なら `SeekAsync(duration - OutroSeconds)` → `SetVolumeAsync(theme.Volume)` → `_clock.DelayAsync(theme.OutroSeconds)`。
7. 呼び出し元（BroadcastEngine）が `PauseAsync(ct)`。

**step の render（合成→ミックス or 順次）**:
```
RenderStepAsync(SpokenStep step, ct) -> IReadOnlyList<byte[]>
  wavs = await Task.WhenAll(step.Voices.Select(v => _tts.SynthesizeAsync(v.Text, v.SpeakerId, ct)))
  if step.Voices.Count == 1: return [ wavs[0] ]
  try    { return [ PcmWavMixer.Mix(wavs) ] }                 // 同時発話＝1 WAV に合成
  catch (WavFormatMismatchException) { warn($"同時発話 step[{i}] 形式不一致 → 順次再生にフォールバック"); return wavs }  // 不一致→順次再生
```
再生は `foreach (var wav in rendered) await _audio.PlayAsync(wav, ct)`（通常 1 要素・フォールバック時のみ複数）。prefetch は「次 step の RenderStepAsync を現 step 再生中に走らせる」。in-flight は finally で観測（既存と同じ）。
- **warn 粒度**: `warn` は**形式不一致の同時グループごとに 1 回**（グローバル 1 回ではない＝同一 RunAsync 内に不一致 step が複数あればその数だけ）。メッセージは step index を含む（合成失敗 `TtsException` は warn でなく throw 伝播）。
- **キャンセル/完全静寂**: 既存 string 版と同一の try/catch fail-safe（例外/キャンセルで `PauseIgnoringCancellationAsync(restoringVolume: theme.Volume)`＝`CancellationToken.None`・§3-1）。`Task.WhenAll` のキャンセルは部分 WAV を返さず `OperationCanceledException` を投げる（中途ミックスは発生しない）。正常終了時の停止は呼び出し元（BroadcastEngine）が `PauseAsync(ct)`（既存と同じ）。

### 3-5. BroadcastEngine OP/ED 分岐（Core）

OP（`:573-581`）/ ED（`:624-633`）を共通ヘルパへ:
```csharp
private async Task RunThemedAsync(ThemedSegment seg, DjProfile main, DjProfile anchor,
    IReadOnlyList<DjProfile> djs, BroadcastThemes themes, IReadOnlyDictionary<string,string> extra, CancellationToken ct)
{
    var spiel = seg.Spiel(main.Id, new[] { anchor.Id }) ?? new DjSpiel("");
    var staging = seg.Staging with { Tagline = spiel.Tagline };
    if (spiel.HasScript)
    {
        var steps = ResolveSpokenSteps(spiel.Script!, djs, themes.Greetings, extra); // main は seg.Spiel(main.Id,..) で使用済み・ここでは不要
        await _themeSequencer.RunAsync(staging, main.SpeakerId, steps, ct).ConfigureAwait(false);
    }
    else
    {
        await _themeSequencer.RunAsync(staging, Expand(themes.Greetings, spiel.Announcement, extra), main.SpeakerId, ct).ConfigureAwait(false);
    }
}
```
`ResolveSpokenSteps`: 各 `SpielLine.Speaker`（DJ id）を `djs` から `DjProfile` 解決（**preflight 済みゆえ存在前提＝未定義 speaker はここには来ない。実行時フォールバックは設けない**＝CONS-12）→ `SpeakerId`、`Text` を `Expand` で展開。**Expand は行ごとに OP 実行時（ResolveSpokenSteps 実行時＝発話直前）に呼ぶ**ため、`{greeting}`/時刻や `{first_song}`（選曲確定後）が陳腐化しない（既存 announcement のチャンク展開と同等のタイミング）。

**preflight 追加（fail-fast＝`E-CFG-MISSING-FIELD-001`）**: 起動時 preflight に「`themes.Opening.ByDj` と `themes.Ending.ByDj` の各 script の全 speaker が `djs` に存在」を検証（不在→fail-fast）。なお各 by_dj エントリが announcement XOR script を満たすことは**ローダ（§4-2）が保証**するため、`Spiel(...)` が返す `DjSpiel` は常に妥当（HasScript=true なら Script 非空・false なら Announcement 非空）。`Spiel` が null（ByDj 該当なし）の `?? new DjSpiel("")` フォールバックは HasScript=false・Announcement 空＝**無音（BGM のみ）に degrade**（CONS-8。BuildThemed は by_dj 空を別途 fail-fast）。

---

## 4. config・スキーマ拡張

### 4-1. `themes.yaml`（`opening.by_dj.<dj>` に `script` 追加・排他）

```yaml
opening:
  track_uri: "..."        # 既存 staging は不変
  intro_seconds: 5
  volume: 100
  ducked_volume: 35
  outro_seconds: 10
  by_dj:
    zundamon:                       # 従来どおり（announcement）＝無改修
      tagline: "ずんだもんの ケイラボAIラジオ"
      announcement: "{greeting}。{month}月{day}日、{ampm}{hour}時になりました。…"
    reimu:                          # 多声化（script・announcement は書かない）
      tagline: "ゆっくり ケイラボAIラジオ"      # 任意（仮）
      script:
        - speaker: reimu
          line: "ゆっくり霊夢です。（仮）"
        - speaker: marisa
          line: "ゆっくり魔理沙だぜ。（仮）"
        - speaker: reimu
          line: "{greeting}。{month}月{day}日、{ampm}{hour}時。ケイラボAIラジオの時間よ。…（クレジット: 音声は AquesTalk と VOICEVOX を使用）（仮）"
        - simultaneous:             # 同時発話グループ
            - speaker: reimu
              line: "ゆっくりしていってね。（仮）"
            - speaker: marisa
              line: "ゆっくりしていってね。（仮）"
```

各 script エントリは **`{speaker, line}`（単独）** または **`{simultaneous: [{speaker, line}, …]}`（同時）** のいずれか。

### 4-2. ローダ DTO / 検証（`ThemesConfig.cs`）

```csharp
public sealed class SpielDto
{
    public string? Tagline { get; set; }
    public string? Announcement { get; set; }
    public List<ScriptStepDto>? Script { get; set; }   // 追加
}
public sealed class ScriptStepDto
{
    public string? Speaker { get; set; }                       // 単独行
    public string? Line { get; set; }
    public List<ScriptLineDto>? Simultaneous { get; set; }     // 同時グループ
}
public sealed class ScriptLineDto { public string? Speaker { get; set; } public string? Line { get; set; } }
```

`BuildThemed` の各 by_dj 検証（fail-fast＝`E-CFG-MISSING-FIELD-001`）:
- **XOR 判定**: 「`announcement` が非 null かつ非空文字列」 XOR 「`script` が非 null かつ要素数 > 0」。**`script: []`（空リスト）や省略/null は script 不在扱い**＝announcement 側を要求。両立（announcement 非空＋script 非空）も両無（announcement 空＋script 空/null）も不正（EMPTY-SCRIPT）。
- script の各 step: `simultaneous` が**存在するなら要素数 ≥ 1** かつ各要素の `speaker`/`line` 非空（`simultaneous: []` 空グループは不正＝TC-ENGINE-2）。`simultaneous` 無しなら `speaker`/`line` 非空。**＝どの SpielStep も Voices ≥ 1 を保証**（RenderStepAsync は Voices ≥ 1 前提）。
- DTO→ドメイン: step に `simultaneous` あり→ `SpielStep(simultaneous.Select(x => new SpielLine(x.Speaker, x.Line)))`、無し→ `SpielStep([ new SpielLine(speaker, line) ])`。
- `Announcement` は script 時 `""`（空）で `DjSpiel(Announcement:"", Tagline, Script)`。

News（単一 ThemeConfig）は無関係。ED（`ending.by_dj`）も同じ BuildThemed を通すため script 可（本スライスでは ED は announcement のまま据え置き＝config 変更なし）。

---

## 5. エラーコード

**新エラーコードを追加しない**（CLAUDE.md §4-3・既存台帳維持）。
- announcement/script 排他違反・script 内 speaker/line 欠落・OP/ED script speaker が djs 不在＝すべて `E-CFG-MISSING-FIELD-001`（fail-fast・起動時 config 不正）。
- ミキサー形式不一致＝`WavFormatMismatchException`（形式不一致を検出し throw・**エラーコード割り当てなし**の内部シグナル）→ ThemeSequencer が握り潰して順次再生＋WARN（fail-tolerant・診断はログのみ）。
- 合成失敗（`TtsException`）は既存どおり ThemeSequencer→BroadcastEngine の fail-tolerant 経路（OP が critical なら放送中止＝既存挙動・parity）。

---

## 6. テスト計画（xUnit・`dotnet test AIRadio.slnx` 緑＋0 警告）

- **`PcmWavMixer` / `Wav`**（Core・純粋）:
  - 同長 2 WAV → サンプルごとの和（クリップ無し範囲）。
  - 異長 → 最長に 0 詰め（短い方の末尾が無音）。
  - クリップ → `+30000 + 30000 → 32767` / `-30000 + -30000 → -32768`。
  - ヘッダ正当性 → `Wav.Parse(Mix(...))` が rate=8000/channels=1/bits=16・dataSize 一致。
  - 形式不一致（8kHz と 16kHz など）→ `WavFormatMismatchException`。
  - `Wav.Parse` は data チャンクが 44 以外オフセットの WAV でも PCM を正しく取り出す（堅牢性・LIST チャンク等を挟んだ合成 WAV）。
  - `Wav.BuildPcmWav` の出力が `AquesTalk1Interop.EmptyWav()` と同レイアウト（44 byte ヘッダ）。
  - **往復**: `Wav.Parse(Wav.BuildPcmWav(rate,ch,bits,pcm))` が rate/ch/bits＋**PCM サンプル byte 列まで完全一致**（TC-SPEC-2）。
  - **`PcmWavMixer.Mix` 前提**: 空リスト `Mix([])` は契約外（前提 Count≥2）。テストでは契約を文書化（呼び出し側が単一/空を渡さない＝ThemeSequencer がガード）か `ArgumentException` を確認（実装で選択・TC-MIX-4）。
- **`DjSpiel` / `SpielStep` モデル**: `HasScript`・等価性・後方互換（既存 2 引数生成が無改修で通る）。
- **`ThemesConfig`**:
  - announcement のみ → 従来 DjSpiel（Script=null）。
  - script（単独＋simultaneous 混在）→ SpielStep 列に正しく写像。
  - announcement と script 両方 → `E-CFG-MISSING-FIELD-001`。両方無し → `E-CFG-MISSING-FIELD-001`。
  - script の simultaneous 内 speaker/line 欠落 → `E-CFG-MISSING-FIELD-001`。
  - 既存 themes.yaml（announcement のみの zundamon 等）が無改修で緑（後方互換）。
  - **本番 config ロード**: 実 `config/themes.yaml` を読み、全 by_dj（zundamon/metan/tsumugi/reimu/marisa）が `DjSpiel` に解釈され、announcement のみのエントリは `Script=null`（HasScript=false）になる（TC-BACKWARD-1）。
- **`ThemeSequencer` 多声オーバーロード**（fake TTS/Audio/Spotify/Clock）:
  - イベント順序: tagline → BGM Play → Vol(full) → Vol(duck) → 各 step 再生 → Seek → Vol(full) → （呼び出し元 Pause）。
  - 単独 step → `_tts` 1 回・`_audio` 1 回。
  - 同時 step（同形式 fake WAV 2 つ）→ `_tts` 2 回・`PcmWavMixer.Mix` 経由で `_audio` **1 回**（ミックス済み 1 WAV）。
  - 同時 step（形式不一致 fake WAV）→ 順次再生フォールバック（`_audio` 複数回）＋ warn が**そのグループにつき 1 回**。
  - warn 粒度: 不一致 step が 2 つ → warn 2 回（グループごと・TC-SEQU-3）。warn メッセージに step index を含む。
  - prefetch: step[1] の合成が step[0] 再生**前**に開始（既存 prefetch テストと同型）。
  - steps 0 件 → 発話なしで BGM scaffold のみ（`EmptyAnnouncement` parity・intro/duck/outro は実行）。
  - 事前 cancel → `OperationCanceledException`・fail-safe pause。
  - 合成中 cancel（同時 step の `Task.WhenAll` 進行中）→ 両声がキャンセル・中途ミックスなし・fail-safe pause（TC-CANCEL-1）。
  - 再生中 cancel → fail-safe で `PauseIgnoringCancellationAsync`（`CancellationToken.None`）・in-flight prefetch を finally で観測。
- **`BroadcastEngine` OP（script）**:
  - reimu main の OP に script を与えると、ThemeSequencer の多声版が呼ばれ、各 step の speaker が正しい SpeakerId（reimu=1001/marisa=1002）に解決される（fake で speakerId を記録）。
  - 行のプレースホルダ（`{greeting}` 等）が展開される。
  - script 無しの OP（zundamon main）は従来 string 版が呼ばれる（後方互換）。
  - preflight: OP/ED script が djs 不在 speaker を参照 → 起動時 `E-CFG-MISSING-FIELD-001`。
- 既存テスト（ThemeSequencerTests・BroadcastEngineTests・ThemesConfigTests・各エンジン）が**無改修で緑**。

---

## 7. 実装の刻み（順序・各刻みで checkpoint）

1. **Core util**: `Wav`（Parse/BuildPcmWav）＋`PcmWavMixer`＋`WavFormatMismatchException` ＋テスト。`AquesTalk1Interop.EmptyWav()` を `Wav.BuildPcmWav` へ寄せるかは出力一致を確認して判断（一致しないなら据え置き）。
2. **Core モデル**: `SpielLine`/`SpielStep`/`DjSpiel.Script`＋`VoiceLine`/`SpokenStep` ＋モデルテスト。
3. **ThemesConfig**: DTO＋`BuildThemed` 拡張（排他検証・DTO→ドメイン）＋テスト。
4. **ThemeSequencer** 多声オーバーロード（render＝合成/ミックス/順次・prefetch・fail-safe・warn）＋テスト。
5. **BroadcastEngine**: OP/ED を `RunThemedAsync` へ＋`ResolveSpokenSteps`＋preflight 追加 ＋テスト。
6. **themes.yaml**: reimu の OP を script 化（placeholder 文言）。`ThemeSequencer` ctor warn 配線（`BroadcastComposition`）。
7. **`dotnet test` 緑・0 警告** → 多角レビュー Workflow → commit/push → Confluence ミラー → memory。

---

## 8. Win 固有 注記（Mac 参照なし）

| # | 項目 | 方針 |
|---|---|---|
| W-1 | 多声 OP | Mac 版に無い Windows 固有機能（霊夢/魔理沙＝AquesTalk）。 |
| W-2 | 同時発話 | 事前 PCM ミックス（同一 8kHz/16bit/mono）。リアルタイム多重再生はしない（NAudio 単一 WaveOutEvent・パイプライン無改修）。 |
| W-3 | クロス形式 | VOICEVOX 混在は未対応（順次フォールバック＋WARN）。将来リサンプル。 |
| W-4 | スキーマ | announcement XOR script の後方互換（W19b/W-AQT の末尾 optional 前例）。 |
| W-5 | エラー | 新コードなし。config 不正＝`E-CFG-MISSING-FIELD-001`、ミックス不一致＝ログのみ。 |

---

## 9. 受け入れ条件

テスト（`dotnet test AIRadio.slnx` 緑・0 警告）:
- [ ] `PcmWavMixer`/`Wav`: 加算・クリップ・ゼロ詰め・ヘッダ・形式不一致・堅牢 Parse。
- [ ] `ThemesConfig`: script（単独＋同時）写像・排他検証・後方互換。
- [ ] `ThemeSequencer` 多声: 順序・単独/同時（ミックス 1 WAV）・不一致フォールバック＋warn・prefetch・空・cancel。
- [ ] `BroadcastEngine` OP: speaker 解決・行展開・script 有/無の分岐・preflight fail-fast。
- [ ] 既存テスト無改修で緑（regression 0）＝実装前に `ThemeSequencerTests`／`BroadcastEngineTests`／`ProgramThemesConfigTests` の baseline を記録し、実装後に `dotnet test AIRadio.slnx` 全体で同一緑＋新規分を確認。

実機（VOICEVOX＋AquesTalk DLL 配置後・最後に一括）:
- [ ] 火/木/土の OP が多声台本で流れる（①霊夢→②魔理沙→③霊夢本編→④二人同時）。
- [ ] ④の「二人同時」が**重なって**聞こえる（PCM ミックス）。
- [ ] OP の BGM ダッキング（intro→duck→発話→outro→フル）が多声でも崩れない。
- [ ] zundamon/metan/tsumugi 日の OP は従来どおり単一話者で流れる（後方互換）。
- [ ] 多声 OP 中の停止で即静寂（完全静寂 §3-1）。

---

## 付録: 参照
- 現行 seam: `AIRadio.Core/Program.cs`（DjSpiel/ThemedSegment:352-396）・`AIRadio.Core/BroadcastEngine.cs`（OP:573-581 / ED:624-633 / Expand:658-666 / preflight:128-205）・`AIRadio.Core/ThemeSequencer.cs`（RunAsync/ChunkAnnouncement）・`AIRadio.Core/Abstractions.cs`（ITTSBackend:15 / IAudioPlayer:21）・`AIRadio.Infrastructure/Audio/NAudioPlayer.cs`（WaveFileReader 再生）・`AIRadio.Infrastructure/Tts/AquesTalk1Synthesizer.cs`（EmptyWav:14-37 / SynthesizeAsync:101）・`AIRadio.Infrastructure/Tts/RoutingTTS.cs`（BuildVoiceMap/SynthesizeAsync）・`config/themes.yaml`（opening.by_dj）・`config/djs.yaml`（reimu 1001/f1・marisa 1002/f2）・`config/program.yaml`（weekly_cast 火木土=[reimu,marisa]）。
