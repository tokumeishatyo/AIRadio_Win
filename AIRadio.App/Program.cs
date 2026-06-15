using AIRadio.Core;
using AIRadio.Infrastructure;

// ケイラボAIラジオ (Windows) — W0 スケルトン。
// Debug ビルドはコンソールサブシステム（OutputType=Exe）で全ログが標準出力に流れる（Mac 版のコンソール起動を踏襲）。
// 実エンジン（VOICEVOX / Gemini / Spotify）とトレイ常駐 UI は後続スライス（W1+ / W9）で実装する。

IRadioLog log = new ConsoleRadioLog();
log.Log("ケイラボAIラジオ (Windows) — W0 スケルトン起動");
log.Log("Core 抽象 / ドメイン型 / テスト土台のみ。実エンジンとトレイ UI は後続スライス。");
log.Log("W0 OK: 起動・ログ確認用スタブ。終了します。");
