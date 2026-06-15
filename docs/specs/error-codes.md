# エラーコード台帳

> Mac 版 `error-codes.md` のミラー（カテゴリ・方針は同一）。Windows 版のスライス番号（W*）に対応付ける。

形式: `E-<CAT3>-<DETAIL>-<NNN>`。C# では例外 `RadioError` に安定コード文字列（`Code` プロパティ）を持たせる。

カテゴリ: CFG（設定）/ RTM（実行時）/ SPT（Spotify）/ TTS（VOICEVOX・AquesTalk）/ LLM（Gemini）/
NEWS（News RSS）/ WX（気象庁天気）/ RES（リサーチ共通）/ ART（アーティスト特集）/ JNL（長期記憶）/
PRON（読み辞書）。

ART は **実行時の特集スキップ専用**（fail-tolerant・throw しない・診断ログ用の安定コード）。
特集の設定不正（コーナー不在・id 衝突・`artists.yaml` 破損）は既存 CFG（`E-CFG-MISSING-FIELD-001`）に寄せる。

JNL（ステーション・ジャーナル, W18）は **完全 fail-tolerant**: 要約 LLM 失敗・ファイル破損・保存失敗のいずれも
握り潰して放送を継続する（専用の throw コードは設けず、診断はログのみ）。記録は ED で正常終了した回のみ。

PRON（読み辞書, W19a）も **完全 fail-tolerant**: VOICEVOX 未起動・個別エントリ拒否（422・非カタカナ読み）でも
握り潰して放送を継続する（専用の throw コードは設けず、診断はログのみ）。設定破損は CFG（`E-CFG-MISSING-FIELD-001`）。

| コード | カテゴリ | 発生条件 | 導入スライス |
|---|---|---|---|
| `E-SPT-NO-DEVICE-001` | SPT | 再生可能な Spotify がない | W0 |
| `E-SPT-API-FAILED-001` | SPT | Spotify 操作の一般失敗 | W0 |
| `E-CFG-MISSING-FIELD-001` | CFG | 設定の必須フィールド欠落 | W0 |
| `E-TTS-UNREACHABLE-001` | TTS | VOICEVOX に接続できない | W1 |
| `E-TTS-SYNTHESIS-FAILED-001` | TTS | 合成 API がエラー応答 | W1 |
| `E-RTM-AUDIO-PLAYBACK-001` | RTM | 音声再生の開始に失敗 | W1 |
| `E-SPT-AUTH-FAILED-001` | SPT | PKCE トークン交換／更新失敗 | W2 |
| `E-SPT-SEARCH-FAILED-001` | SPT | 検索 / トラック取得失敗 | W2 |
| `E-SPT-AUTH-REQUIRED-001` | SPT | 未ログイン（refresh トークンなし）。PKCE 認可が必要 | W2 |
| `E-NEWS-FETCH-FAILED-001` | NEWS | Google News RSS 取得・解析失敗 | W5 |
| `E-WX-FETCH-FAILED-001` | WX | 気象庁 取得・解析失敗 | W5 |
| `E-LLM-KEY-MISSING-001` | LLM | Gemini API キー未設定（`llm.local.yaml` なし / `api_key` 空）| W6 |
| `E-LLM-API-FAILED-001` | LLM | `generateContent` が非 2xx / 通信失敗 | W6 |
| `E-LLM-EMPTY-RESPONSE-001` | LLM | LLM 応答にテキストがない | W6 |
| `E-LLM-SCRIPT-PARSE-FAILED-001` | LLM | LLM 応答が台本として解釈できない（4 行未満）| W6 |
| `E-RTM-SEGMENT-FAILED-001` | RTM | 放送セグメントが実行時エラーで中断（スキップして継続）| W7 |
| `E-ART-EMPTY-POOL-001` | ART | 特集有効だが `artists.yaml` が空／未生成 → 特集スキップ（throw しない・診断ログ用）| W15 |
| `E-ART-INSUFFICIENT-TRACKS-001` | ART | 再生可能曲が 3 曲未満 → 特集スキップ（throw しない・診断ログ用）| W15 |
| `E-PRON-SYNC-UNREACHABLE-001` | PRON | VOICEVOX に接続できず読み辞書を同期できない（GET 失敗。ログのみ・放送継続）| W19a |
| `E-PRON-WORD-REJECTED-001` | PRON | 個別エントリの登録/更新が拒否（422 等）／読みが非カタカナ → そのエントリのみスキップ（ログのみ）| W19a |

> AquesTalk1（W-AQT）のエラーは TTS カテゴリに追加予定（DLL ロード失敗・合成失敗・かな変換失敗）。最終スライス着手時に起票。
