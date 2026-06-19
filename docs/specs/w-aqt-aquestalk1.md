# W-AQT — AquesTalk1 TTS バックエンド（最終スライス）

> 仕様駆動ワークフロー（CLAUDE.md §7）の SoT。**Windows 固有スライス＝Mac リファレンスなし**（Mac 版に AquesTalk は存在しない）。
> design.md §3 TTS 行（line 85）・§6 スライス表 W-AQT 行（line 159）・§8（line 184）・§9（line 194）を実体化。
> 旧 Windows 版（`../AIRadio-main/`）は **AquesTalk10 + AqKanji2Koe** 構成で、4 層アーキ・marshalling・AqKanji2Koe・config/secret・registry を**構造として流用**するが、合成関数シグネチャ・声種選択・サンプルレートは AquesTalk1.0 用に置換する。
> ユーザー決定（2026-06-18 設計 Q&A）: ①AquesTalk1.0 + AqKanji2Koe の**有償開発ライセンス取得済み**（dev key あり・評価制限解除）。**使用ライセンスキーも取得済み**（透かし除去）。②**x64 版**を使用（in-process P/Invoke・IPC ホスト不要）。③漢字→かな＝**AqKanji2Koe 純正**。④per-DJ 切替＝**DjProfile に任意フィールド追加**。⑤声種は**複数同時**（霊夢・魔理沙＋可能な限り）。

---

## 1. 概要・スコープ

VOICEVOX に加え **AquesTalk1.0（初代）** を 2 つ目の TTS エンジンとして導入し、DJ・ゲストごとに `djs.yaml` / `guests.yaml` の `tts_backend: voicevox|aquestalk` で選択できるようにする。AquesTalk DJ は AquesTalk1.0 の声種別 DLL（f1/f2/…）で合成する。

**含むもの**:
1. AquesTalk1.0 ネイティブ層（声種別 DLL の**動的マルチモジュール**ロード）＋ AqKanji2Koe（漢字→音声記号列）。
2. `AquesTalk1Synthesizer`（漢字→koe→声種別 synth → 8kHz WAV）。
3. `RoutingTTS`（`ITTSBackend` 合成）= speaker_id で VOICEVOX / AquesTalk を振り分け。
4. `DjProfile` への任意 2 フィールド追加（`TtsBackend` / `AquesTalkVoice`）＋ローダ拡張。
5. config（`tts.yaml` の `aquestalk:` 節）＋ secret（`aquestalk.local.yaml`＝gitignore）＋ native/ レイアウト。
6. TTS カテゴリのエラーコード 4 個追加。

**スコープ外**:
- **条件付きキャスティング**（「霊夢・魔理沙 DJ 回のゲストは AquesTalk」のような、当日のメイン DJ に応じてゲストの backend を変える規則）は**本スライス対象外**。W-AQT は per-DJ / per-guest backend 選択の**機構**を提供する。具体的な割当（誰がどの声か・ゲストの backend）は config（finalize フェーズ）で決める。ゲスト backend を当日 DJ に連動させる規則が必要なら別スライス。
- AqKanji2Koe ユーザー辞書（`AqUsrDic` による独自読み登録）。VOICEVOX 側の読み辞書（W19a/W19b）とは別系統。将来拡張。
- `AquesTalk_Synthe` の SJIS / UTF-16 入口、`ConvRoman`（pico 用ローマ字）。UTF-8 入口（`_Utf8`）のみ使用。

**不変条件**: `ITTSBackend` シグネチャ・既存エンジン（`CornerEngine` / `ArtistFeatureEngine` / `ThemeSequencer` / `BroadcastEngine`）・`NAudioPlayer` は**無改修**。ニュース・天気の読み手＝青山龍星（VOICEVOX, speaker_id 13）は既定のまま不変。

---

## 2. 確定 API（実ヘッダ準拠・推測なし）

`aqtk1_win/lib64/AquesTalk.h`（Ver.2.0, 2025-04）・`aqk2k_win/lib64/AqKanji2Koe.h`（Ver.4.21）より確定。**全関数 `__stdcall`**。

### 2-1. AquesTalk1.0（`AquesTalk.dll`・声種ごとに別ファイル）
```c
// 音声記号列(koe) → WAV。出力 = 8kHz / 16bit / monaural（RIFF ヘッダ込み）。
unsigned char* __stdcall AquesTalk_Synthe_Utf8(const char* koe, int iSpeed, int* pSize);
//   koe   : UTF-8・NULL 終端・BOM なし
//   iSpeed: 発話速度[%] 50-300（100=標準・大きいほど速い）
//   pSize : [out] 成功時=生成データの byte 長 / 失敗時=エラーコード（戻り値 NULL）
void __stdcall AquesTalk_FreeWave(unsigned char* wav);          // 合成バッファ解放（必須）
int  __stdcall AquesTalk_SetDevKey(const char* key);            // 開発キー（評価制限ナ/マ行→ヌ を解除）
int  __stdcall AquesTalk_SetUsrKey(const char* key);            // 使用キー（音声透かし除去）
```
- **声種＝DLL 差替**。`aqtk1_win/lib64/<voice>/AquesTalk.dll` が声種ごとに存在: `f1 f2 f3 m1 m2 dvd imd1 jgr r1`（計 9 種・全 x64 完備）。声種選択パラメータは無く、**どの `AquesTalk.dll` をロードしたか**で決まる。
- `SetDevKey` / `SetUsrKey` は**合成前に一度**呼ぶ。各声種モジュールは別モジュール状態を持つため、**ロードした各 `AquesTalk.dll` ごとに**両キーを設定する。
- 戻り NULL 時の主なエラー（`pSize`）: 105 未定義音声記号 / 106 タグ不正 / 107 タグ長すぎ / 108 タグ値不正 / 200,202,204 記号列長すぎ / 201 1 句の記号数超過 / 100 その他 / 101,203 メモリ不足。

### 2-2. AqKanji2Koe（`AqKanji2Koe.dll`・漢字かな交じり文 → 音声記号列）
```c
void* __stdcall AqKanji2Koe_Create(const char* pathDic, int* pErr);   // pathDic=aqdic.bin を含む dir。失敗時 0
void  __stdcall AqKanji2Koe_Release(void* h);
int   __stdcall AqKanji2Koe_Convert_utf8(void* h, const char* kanji, char* koe, int nBufKoe); // 0=成功・出力 UTF-8
int   __stdcall AqKanji2Koe_SetDevKey(const char* devKey);            // 開発キー（評価制限解除＝未設定だと漢字→koe 段でナ/マ行が「ヌ」化）
```
- **旧資産の P/Invoke 宣言（`AquesTalkNative.cs` の AqKanji2Koe 部）と完全同一** → そのまま流用（ただし本スライスは動的ロードに統一・下記 §4-1）。
- 辞書 `aq_dic/`（`aqdic.bin` システム辞書 + `aq_user.dic` ユーザー辞書 + `CREDITS`）。`AqKanji2Koe_SetUsrKey` は**存在しない**（dev key のみ）。

---

## 3. 確定設計判断

1. **声種＝動的マルチモジュール**（霊夢/魔理沙＋可能な限り＝複数声種同時）。`[DllImport("AquesTalk.dll")]` は同名 1 モジュールにしか束縛できないため使えない。`NativeLibrary.Load(フルパス)` で声種ごとに別モジュールとして読み込み、`NativeLibrary.GetExport` ＋ `[UnmanagedFunctionPointer(CallingConvention.StdCall)]` デリゲートで呼ぶ。
2. **`ITTSBackend` 不変** → AquesTalk 専用の合成は `AquesTalk1Synthesizer`（具象）に置き、`RoutingTTS`（`ITTSBackend`）が VOICEVOX と振り分ける。エンジン・`NAudioPlayer` 無改修。
3. **per-DJ ルーティングは speaker_id マップ**。`ITTSBackend.SynthesizeAsync(text, speakerId, ct)` には speaker_id しか流れないため、`RoutingTTS` は `speaker_id → 声種` マップ（aquestalk DJ から構築）で振り分ける。**AquesTalk DJ の speaker_id は他の全 DJ と衝突しない一意 ID**（ルーティングキー兼用）。load 時に衝突を検証（衝突は `E-CFG-MISSING-FIELD-001`／一意性違反として fail-fast）。
4. **Core 変更は DjProfile への任意 2 フィールドのみ**（`TtsBackend="voicevox"` / `AquesTalkVoice=null` の末尾 optional positional。W19b `Reading` と同じ後方互換）。design.md の **§3 line 85「Core 変更不要」と §6 line 159「Core 変更なし」の両方**を**「最小・非破壊の Core 追加」へ注記訂正**する（§8 step 1）。
5. **出力 8kHz / 16bit / mono**（旧 AquesTalk10 の 16kHz ではない）。AquesTalk が返す WAV はヘッダ込みのため、そのまま `ITTSBackend` の戻り `byte[]` として返す（Duration 計算不要＝新 `ITTSBackend` は WAV bytes のみ）。
6. **入力エンコーディングは UTF-8**（`_Utf8` 入口）。VOICEVOX の `NormalizeForSpeech`（波ダッシュ→長音）は VOICEVOX 固有のため**使わない**。AqKanji2Koe に生テキストを渡す。
7. **ライセンスキーは `aquestalk.local.yaml`（gitignore）**。AquesTalk dev/usr key と AqKanji2Koe dev key の 3 種。コミット対象ファイルにキーを一切書かない（CLAUDE.md §3-3）。
8. **実行時失敗は fail-tolerant**（VOICEVOX と同じく `TtsException` を throw → 既存の非 critical セグメント握り潰し経路でスキップ・放送継続）。**起動時の設定欠落のみ fail-fast**（`E-CFG-MISSING-FIELD-001`）。エンジン初期化失敗（DLL/辞書不在）は composition で握り潰し、当該 DJ を無音にフォールバック（§5-3）。

---

## 4. アーキテクチャ（4 層・`ITTSBackend` 不変）

```
ITTSBackend（既存・不変： Task<byte[]> SynthesizeAsync(string text, int speakerId, ct)）
  └─ RoutingTTS（新/Infra）                     speaker_id で voicevox/aquestalk 振り分け
       ├─ VoicevoxTTS（既存）
       └─ AquesTalk1Synthesizer（新/Infra・具象） 漢字→koe(AqKanji2Koe)＋声種別 synth → 8kHz WAV
            └─ INativeAquesTalk1（新/Infra・テスト seam）
                 └─ AquesTalk1Native（新/Infra）  NativeLibrary.Load(フルパス)＋GetExport＋__stdcall デリゲート
```

### 4-1. `AquesTalk1Native`（P/Invoke・動的ロード）
```csharp
// AIRadio.Infrastructure/Tts/AquesTalk1Native.cs
internal interface INativeAquesTalk1 : IDisposable
{
    // AqKanji2Koe（1 インスタンス）
    IntPtr CreateKanjiToKoe(string dictDir, string? aqk2kDevKey, out int errCode);
    string ConvertKanjiToKoe(IntPtr handle, string japaneseText, out int errCode);
    void   ReleaseKanjiToKoe(IntPtr handle);
    // AquesTalk1.0（声種別モジュール・初回呼び出し時にロード＋キー設定してキャッシュ）
    byte[] Synthesize(string voiceId, string koe, int speed, out int errCode);
}

public sealed class AquesTalk1Native : INativeAquesTalk1
{
    // ctor(string voicesDir, string? aqk2kDllDir, string? aqtkDevKey, string? aqtkUsrKey)
    //   voicesDir   = …/native/aqtk1（この下に <voice>/AquesTalk.dll）
    //   aqk2kDllDir = …/native/aqk2k（AqKanji2Koe.dll の dir。Load 用）
    // 声種モジュールは Dictionary<string,(IntPtr module, Synthe デリゲート, FreeWave デリゲート)> でキャッシュ。
    //   初回 Synthesize(voiceId,…): NativeLibrary.Load(Path.Combine(voicesDir, voiceId, "AquesTalk.dll"))
    //     → GetExport("AquesTalk_Synthe_Utf8"/"AquesTalk_FreeWave"/"AquesTalk_SetDevKey"/"AquesTalk_SetUsrKey")
    //     → SetDevKey(aqtkDevKey)/SetUsrKey(aqtkUsrKey)（非空のみ）→ cache。
    // marshalling 定石（旧 AppNativeAquesTalk から流用）:
    //   入力: AppendNullTerminator(Encoding.UTF8.GetBytes(text))
    //   出力 WAV: wavPtr==Zero → errCode=size; return []; else Marshal.Copy(wavPtr,wav,0,size); finally FreeWave(wavPtr)
    //   koe 出力: Array.IndexOf((byte)0) で切る → Encoding.UTF8.GetString
    // 同期: 全 native 呼び出しを 1 つの lock で直列化（放送はそもそも 1 行ずつ逐次・安全側）。
    // Dispose: ロード済み全声種モジュールを NativeLibrary.Free（AqKanji2Koe ハンドルは Synthesizer 側で Release）。
}
```
- **デリゲート宣言** は `[UnmanagedFunctionPointer(CallingConvention.StdCall)]`。`AquesTalk_Synthe_Utf8` = `delegate IntPtr (byte[] koe, int iSpeed, out int pSize)`、`AqKanji2Koe_Create` = `delegate IntPtr ([MarshalAs(LPStr)] string pathDic, out int pErr)`、等。
- AqKanji2Koe も `NativeLibrary.Load(Path.Combine(aqk2kDllDir,"AqKanji2Koe.dll"))` で動的ロード（同名衝突回避・声種と統一）。`SetDevKey(aqk2kDevKey)` → `Create(dictDir)`。
- **DLL ロード失敗の区別（T03）**: 声種別 `AquesTalk.dll` は初回 `Synthesize` 時に遅延ロードする。`NativeLibrary.Load` 失敗（`DllNotFoundException`/`BadImageFormatException`＝不在・x86/x64 不一致）は、合成エラー（NULL＋コード 105-204）と**区別できる専用シグナル**（予約 errCode もしくは専用例外）で Synthesizer 層へ返し、`E-TTS-AQT-LOAD-FAILED-001` にマップする（synth 失敗の `SYNTHESIS-FAILED` に混ぜない）。AqKanji2Koe.dll のロード/`Create` 失敗は ctor 段で `INIT-FAILED`（§5-3 フォールバックで握り潰し）。
- **koe バッファ**: `max(8192, utf8(text).Length*8 + 256)` byte。`Convert` が "バッファ不足" を返したらそのまま `E-TTS-AQT-KANA-FAILED-001`（行スキップ）。
- **dict パスの文字コード**: `AqKanji2Koe_Create` は `const char*`（ANSI/CP932）。`native/` はアプリ配下（`AppContext.BaseDirectory`）なので通常 ASCII。CP932 外の文字を含むパスは非対応（注記）。

### 4-2. `AquesTalk1Synthesizer`（具象・orchestration）
```csharp
// AIRadio.Infrastructure/Tts/AquesTalk1Synthesizer.cs
public sealed class AquesTalk1Synthesizer : IDisposable
{
    private const int SampleRate = 8000, Channels = 1, BitsPerSample = 16;   // ★旧 16000 から変更
    // ctor(INativeAquesTalk1 native, string dictDir, string? aqk2kDevKey, int speed=100)
    //   native.CreateKanjiToKoe(dictDir, aqk2kDevKey, out err); ハンドル 0 → throw TtsException.AquesTalkInitFailed
    public Task<byte[]> SynthesizeAsync(string voiceId, string text, CancellationToken ct = default)
    {
        // ct.ThrowIfCancellationRequested();
        // text 空白のみ → BuildEmptyWav(8000)（旧 BuildEmptyWav を SampleRate=8000 で流用）
        // koe = native.ConvertKanjiToKoe(handle, text, out cErr); cErr!=0 → throw TtsException.AquesTalkKanaFailed
        // wav = native.Synthesize(voiceId, koe, speed, out sErr);
        //   声種DLLロード失敗シグナル → throw TtsException.AquesTalkLoadFailed（E-TTS-AQT-LOAD-FAILED-001。T03）
        //   それ以外の sErr!=0 || wav.Length==0（NULL・記号列エラー 105-204） → throw TtsException.AquesTalkSynthesisFailed
        // return Task.FromResult(wav);   // AquesTalk の WAV はヘッダ込み・そのまま返す
    }
    public void Dispose() => _native.ReleaseKanjiToKoe(_handle);  // ＋ _native.Dispose() は所有者（composition）が呼ぶ
}
```
- 失敗は `TtsException`（新 AQT コード）を throw（VOICEVOX と同じ throw 契約 → 既存の非 critical セグメント握り潰しで放送継続）。

### 4-3. `RoutingTTS`（`ITTSBackend` 合成）
```csharp
// AIRadio.Infrastructure/Tts/RoutingTTS.cs
public sealed class RoutingTTS : ITTSBackend
{
    // ctor(ITTSBackend voicevox, AquesTalk1Synthesizer? aquestalk, IReadOnlyDictionary<int,string> voiceBySpeakerId, int speed)
    public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
    {
        // if (voiceBySpeakerId.TryGetValue(speakerId, out var voiceId))
        //     return aquestalk is not null ? aquestalk.SynthesizeAsync(voiceId, text, ct)
        //                                  : Task.FromResult(BuildEmptyWav8k());  // エンジン不在＝無音フォールバック（一度だけ警告ログ）
        // return voicevox.SynthesizeAsync(text, speakerId, ct);
    }
}
```

---

## 5. config・native レイアウト・secret

### 5-1. native/（BYO・`.gitignore` 済み。`aqtk1_win/` `aqk2k_win/` も除外済み）
```
native/
  aqtk1/<voice>/AquesTalk.dll        … 使う声種ぶん（aqtk1_win/lib64/<voice>/ からコピー）
  aqk2k/
    AqKanji2Koe.dll                  … aqk2k_win/lib64/ からコピー
    aq_dic/{aqdic.bin, aq_user.dic, CREDITS}  … aqk2k_win/aq_dic/ からコピー（CREDITS=MARISA/NAIST 辞書の BSD 表記。配布物に同梱必須＝aqk2k readme のライセンス義務）
```
- App.csproj に `<Content Include="native\**" CopyToOutputDirectory="PreserveNewest" />` を追加し bin/native へ複製。ネイティブは `Path.Combine(AppContext.BaseDirectory, voicesDir/dictDir)` で解決。

### 5-2. `config/tts.yaml`（非機密。`aquestalk:` 節を追加）
```yaml
aquestalk:
  voices_dir: "native/aqtk1"   # 声種DLL 親フォルダ（<voice>/AquesTalk.dll）
  dict_dir:   "native/aqk2k"   # AqKanji2Koe.dll と aq_dic/ の親フォルダ
  speed: 100                   # 発話速度[%] 50-300（100=標準）
```
- `TtsConfig` に `AquesTalkVoicesDir` / `AquesTalkDictDir` / `AquesTalkSpeed` を追加（`aquestalk:` 欠落時は既定値＝AquesTalk 未使用なら無害）。

### 5-3. `config/aquestalk.local.yaml`（機密・`*.local.yaml` で gitignore 済み）＋ `.sample`
```yaml
# AquesTalk1 / AqKanji2Koe ライセンスキー（機密・コミット禁止）
aquestalk_dev_key: ""     # AquesTalk1 開発キー（合成段の評価制限ナ/マ行→ヌ を解除）
aquestalk_usr_key: ""     # AquesTalk1 使用キー（音声透かし除去）
aqkanji2koe_dev_key: ""   # AqKanji2Koe 開発キー（★同じナ/マ行→ヌ を上流の漢字→koe 段で解除。両キー必須）
```
- `AquesTalkLocalConfig`（record: 3 キー）＋ tolerant ローダ（欠落/空→空文字。旧 `AquesTalkLocalConfigLoader` の形）。`aquestalk.local.yaml.sample`（空キー）をコミット。
- **エンジン初期化フォールバック**: composition で `AquesTalk1Synthesizer` 生成（＝AqKanji2Koe_Create）を try/catch。失敗（DLL/辞書不在）→ ログ＋`aquestalk=null` で `RoutingTTS` 構築 → AquesTalk DJ は無音（放送は継続）。aquestalk DJ が 1 人もいなければ生成自体スキップ（VOICEVOX のみ）。
- **dev key 空の警告（T04）**: `SetDevKey`/`SetUsrKey` はキー**非空時のみ**呼ぶ。aquestalk DJ がいるのに `aquestalk_dev_key` または `aqkanji2koe_dev_key` が空なら、composition で**一度だけ WARN ログ**（例「AquesTalk 評価版動作: dev key 未設定でナ/マ行が『ヌ』化します」）。合成自体は**成功し throw しない**（無言の音声劣化）ため fail-tolerant 例外経路に乗らない＝明示 WARN で気付けるようにする。

### 5-4. `config/djs.yaml` / `config/guests.yaml`（per-DJ フィールド）
```yaml
djs:
  - id: reimu
    name: "博麗霊夢"
    speaker_id: 1001          # ★VOICEVOX と衝突しない一意 ID（ルーティングキー）
    persona: "…"
    tts_backend: aquestalk    # 既定 voicevox。aquestalk のとき aquestalk_voice 必須
    aquestalk_voice: f1
  - id: marisa
    name: "霧雨魔理沙"
    speaker_id: 1002
    persona: "…"
    tts_backend: aquestalk
    aquestalk_voice: f2
  # 既存（zundamon/metan/tsumugi/ryusei）は無改修＝tts_backend 省略で voicevox。ryusei=ニュース/天気で不変。
```
- `DjProfile` 追加: `record DjProfile(string Id, string Name, int SpeakerId, string Persona, string TtsBackend="voicevox", string? AquesTalkVoice=null)`。
- `DjsConfig.DjDto` / `GuestsConfig.GuestDto` に `string? TtsBackend` / `string? AquesTalkVoice` を追加。Map で `tts_backend ?? "voicevox"`、`aquestalk_voice`。**検証**: `tts_backend=="aquestalk"` かつ `aquestalk_voice` 空 → `E-CFG-MISSING-FIELD-001`（`djs[id].aquestalk_voice`）。未知の `tts_backend` 値（voicevox/aquestalk 以外）→ `E-CFG-MISSING-FIELD-001` 相当で fail-fast。

### 5-5. composition 配線（`BroadcastComposition`）
```
combined = djs ∪ guests
aquestalkDjs = combined.Where(tts_backend==aquestalk)   // (speaker_id, aquestalk_voice)
voicevoxIds  = combined.Where(tts_backend==voicevox).Select(speaker_id)
// ★一意性検証は ToDictionary の前に行う（WAQT-3）。.NET の ToDictionary は重複キーで ArgumentException を投げ、
//   composition の catch(RadioException) を素通りして未捕捉クラッシュになるため:
//   aquestalkDjs.speaker_id を group 化 → 重複(>1) または voicevoxIds と交差があれば
//   throw ConfigException.MissingField("djs/guests[id].speaker_id（一意性違反）")。
// 検証通過後に materialize: aquestalkMap = aquestalkDjs.ToDictionary(speaker_id → aquestalk_voice)
aquestalk = aquestalkMap.Count>0 ? try{ new AquesTalk1Synthesizer(new AquesTalk1Native(voicesDir, dictDir, devKey, usrKey), dictDir+"/aq_dic", aqk2kDevKey, speed) }catch{ log; null } : null
tts = new RoutingTTS(new VoicevoxTTS(...), aquestalk, aquestalkMap, speed)
// 以降 ThemeSequencer/CornerEngine/ArtistFeatureEngine へ tts(=RoutingTTS) を注入（line 74 の VoicevoxTTS を置換）。
// RunAsync の finally で aquestalk?.Dispose()（AqKanji2Koe Release＋声種モジュール Free）。
```

---

## 6. エラーコード（TTS カテゴリ追加・error-codes.md 起票）

`Errors.cs` の `TtsException` に static ファクトリ 4 個追加。`error-codes.md` の表に 4 行追加し、line 43 の予告注記を削除（起票完了）。

| コード | 契機 | 方針 |
|---|---|---|
| `E-TTS-AQT-LOAD-FAILED-001` | `AquesTalk.dll` / `AqKanji2Koe.dll` ロード失敗（不在・x86/x64 不一致） | fail-tolerant（throw→セグメントスキップ） |
| `E-TTS-AQT-INIT-FAILED-001` | `AqKanji2Koe_Create` 失敗（辞書 path 不正・破損） | fail-tolerant（ctor throw→composition で握り潰し・当該 DJ 無音） |
| `E-TTS-AQT-KANA-FAILED-001` | `AqKanji2Koe_Convert_utf8` 失敗（戻り非 0・バッファ不足） | fail-tolerant |
| `E-TTS-AQT-SYNTHESIS-FAILED-001` | `AquesTalk_Synthe_Utf8` が NULL（記号列不正等） | fail-tolerant |

- config の `voices_dir`/`dict_dir`/`aquestalk_voice` 欠落＝`E-CFG-MISSING-FIELD-001` 再利用（fail-fast）。
- 既存 `E-TTS-UNREACHABLE-001` / `E-TTS-SYNTHESIS-FAILED-001`（VOICEVOX）は不変。

---

## 7. テスト計画（xUnit・`dotnet test AIRadio.slnx` 緑＋0 警告）

`INativeAquesTalk1` の **fake** で実 DLL なしに Synthesizer ロジックを検証（実 DLL は実機確認で）。

- `AquesTalk1SynthesizerTests`（fake native）:
  - 漢字→koe→声種別 synth の**呼び出し順序**と voiceId 受け渡し。
  - 空白テキスト → 8kHz 空 WAV（44 byte・convert/synth 未呼び出し）。
  - 出力 SampleRate=8000（空 WAV ヘッダ検査）。
  - Convert 失敗 → `E-TTS-AQT-KANA-FAILED-001`／Synthe 失敗（NULL）→ `E-TTS-AQT-SYNTHESIS-FAILED-001`／Create 失敗（ctor）→ `E-TTS-AQT-INIT-FAILED-001`／**声種DLLロード失敗シグナル → `E-TTS-AQT-LOAD-FAILED-001`**（T03。SYNTHESIS と区別）。
  - キャンセル: 事前 cancel で `OperationCanceledException`。
- `RoutingTTSTests`（fake `ITTSBackend` voicevox ＋ fake/null aquestalk）:
  - speaker_id がマップにある→AquesTalk（voiceId 正）・ない→VOICEVOX。
  - aquestalk=null かつ map ヒット → 空 WAV（無音フォールバック）・VOICEVOX を呼ばない。
- `DjsConfig`/`GuestsConfig` テスト: `tts_backend`/`aquestalk_voice` の読み・既定 voicevox・aquestalk なのに voice 欠落で `E-CFG-MISSING-FIELD-001`・未知 backend で fail-fast。後方互換（フィールド省略 yaml がそのまま通る）。
- composition 一意性検証テスト（aquestalk speaker_id 重複／VOICEVOX と衝突で `ConfigException`）。
- `TtsConfig` テスト: `aquestalk:` 節の読み・欠落時既定。
- `AquesTalkLocalConfig` ローダテスト: 3 キー present/欠落/ファイル無し→空。
- **`AquesTalk1Native` キー適用テスト（T04）**: `SetDevKey`/`SetUsrKey` はキー**非空時のみ**呼ぶ（空キーで未呼出）。
- **dev key 空警告テスト（T04）**: aquestalk DJ ありで `aquestalk_dev_key`/`aqkanji2koe_dev_key` が空なら composition が一度だけ WARN を出す。
- 既存テスト（VoicevoxTTS・DjsConfig 既存・各エンジン）が**無改修で緑**（後方互換）。

---

## 8. 実装の刻み（順序・各刻みで checkpoint）

1. **エラーコード起票＋ design.md 同期**: `Errors.cs` TtsException に 4 ファクトリ＋`error-codes.md` 4 行・line 43 注記削除。design.md 訂正: **§3 line 85「Core 変更不要」** ＋ **§6 line 159「Core 変更なし」** を「最小・非破壊の Core 追加（DjProfile に任意2フィールド）」へ、**§4 line 108 の旧クラス名 `Tts/AquesTalk1Backend + AquesTalk1Native`** を実クラス `Tts/RoutingTTS + AquesTalk1Synthesizer + AquesTalk1Native` へ更新。
2. **native レイアウト整備**: `aqtk1_win/lib64` `aqk2k_win/lib64` `aqk2k_win/aq_dic` から `native/aqtk1/<voice>` `native/aqk2k` へコピー（霊夢=f1・魔理沙=f2＋必要なら他声種）＋ App.csproj に native コピー設定。
3. **`AquesTalk1Native` ＋ `INativeAquesTalk1`**（動的ロード・marshalling）。
4. **`AquesTalk1Synthesizer`** ＋ fake で単体テスト。
5. **`DjProfile` 2 フィールド ＋ DjsConfig/GuestsConfig ＋ tts.yaml/aquestalk.local.yaml(.sample) ＋ ローダ** ＋テスト。
6. **`RoutingTTS`** ＋一意性検証 ＋テスト。
7. **`BroadcastComposition` 配線 ＋ djs.yaml に reimu/marisa**。
8. **`dotnet test` 緑・0 警告** → 多角レビュー Workflow → commit/push → Confluence ミラー → memory。

---

## 9. Win 固有 parity 注記（AquesTalk10 旧資産 → AquesTalk1.0）

| # | 項目 | 旧資産（AquesTalk10） | 本スライス（AquesTalk1.0） |
|---|---|---|---|
| W-1 | 合成シグネチャ | `AquesTalk_Synthe_Utf8(ref AQTK_VOICE, koe, out size)` | `AquesTalk_Synthe_Utf8(koe, iSpeed, out size)`（構造体なし・速度のみ） |
| W-2 | 声種選択 | `AQTK_VOICE` 7 int プリセット（F1..R2） | **声種別 DLL 動的ロード**（f1/f2/…）・速度のみ可変 |
| W-3 | サンプルレート | 16000 | **8000** |
| W-4 | ロード方式 | 静的 `[DllImport]`＋`SetDllImportResolver` | **`NativeLibrary.Load`＋`GetExport`＋デリゲート**（同名複数モジュール） |
| W-5 | ライセンス | dev key（AquesTalk＋AqKanji2Koe 共通運用） | AquesTalk **dev＋usr** ＋ AqKanji2Koe **dev**（3 キー・モジュールごと SetKey） |
| W-6 | AqKanji2Koe | 流用 | **完全同一・流用**（動的ロードに統一） |
| W-7 | 戻り値 | `TtsSynthesisResult`（Duration 等） | **`byte[]`（WAV）のみ**（新 `ITTSBackend`・Duration 不要） |
| W-8 | ルーティング | `Personality.TtsEngine` 文字列＋registry | **speaker_id マップ**（`ITTSBackend` 不変・DjProfile フィールド） |
| W-9 | 呼出規約 | `__stdcall` | `__stdcall`（実ヘッダ確認・同じ） |
| W-10 | テキスト前処理 | （AqKanji2Koe 任せ） | VOICEVOX `NormalizeForSpeech` は**使わない**（AqKanji2Koe に生テキスト） |

---

## 10. 実機確認項目（VOICEVOX＋AquesTalk DLL 配置後・最後に一括）

- [ ] 霊夢（f1）・魔理沙（f2）が AquesTalk1 のゆっくり声で発話する。
- [ ] **AquesTalk1 dev key** 設定済みで合成段のナ/マ行が「ヌ」化しない。
- [ ] **AqKanji2Koe dev key** 設定済みで漢字→koe 段のナ/マ行が「ヌ」化しない（両キー必須・片方空だと該当行が「ヌ」になる）。
- [ ] 使用キー設定で音声透かし（無音域のランダム挿入）が消える。
- [ ] 同一放送内で VOICEVOX DJ と AquesTalk DJ が混在して正しく振り分く（speaker_id 衝突なし）。
- [ ] ニュース・天気は青山龍星（VOICEVOX）で不変。
- [ ] 漢字交じり台本が AqKanji2Koe 経由で自然な読みになる。
- [ ] AquesTalk DLL/辞書を置かない状態でも VOICEVOX DJ は通常放送・AquesTalk DJ は無音で放送継続（fail-tolerant）。
- [ ] x64 プロセスで AquesTalk1.0 x64 DLL がロードされる（bitness 不一致なし）。

---

## 付録: 参照

- 実 SDK: `aqtk1_win/lib64/AquesTalk.h`・`aqk2k_win/lib64/AqKanji2Koe.h`・各 `samples/HelloTalk.cpp`/`Kanji2KoeCmd.cpp`（読み取り専用・gitignore）。
- 旧資産（構造流用元・読み取り専用）: `../AIRadio-main/AIRadio.Infrastructure/Tts/{AquesTalkNative,AppNativeAquesTalk,AquesTalkTtsBackend}.cs`・`AIRadio.Core/Tts/*`・`docs/specs/tts-aquestalk.md`。
- 現行 seam: `AIRadio.Core/Abstractions.cs`（ITTSBackend）・`AIRadio.Core/Dialogue.cs`（DjProfile）・`AIRadio.Infrastructure/Tts/VoicevoxTTS.cs`・`AIRadio.App/Composition/BroadcastComposition.cs`（line 74 配線）。
