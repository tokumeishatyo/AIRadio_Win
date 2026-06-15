using AIRadio.Core;
using AIRadio.Infrastructure;

// ケイラボAIラジオ (Windows) — W1 デモ。
// tts.yaml をロード → VOICEVOX で 1 行合成 → NAudio で再生（聴覚確認）。
// VOICEVOX をローカル起動していないと E-TTS-UNREACHABLE-001 になる。
// Debug ビルドはコンソールに全ログがストリームする（Mac 版のコンソール起動を踏襲）。

IRadioLog log = new ConsoleRadioLog();
log.Log("ケイラボAIラジオ (Windows) — W1 VOICEVOX TTS + 音声再生デモ");

var configPath = Path.Combine(AppContext.BaseDirectory, "config", "tts.yaml");
TtsConfig config;
try
{
    config = TtsConfig.LoadFile(configPath);
}
catch (ConfigException ex)
{
    log.Log($"設定エラー [{ex.Code}]: {ex.Message}（{configPath}）");
    return 1;
}

log.Log($"VOICEVOX endpoint={config.Endpoint} / speed_scale={config.SpeedScale} / volume={config.PlaybackVolume}");

IHttpClient http = new HttpClientAdapter();
ITTSBackend tts = new VoicevoxTTS(config.Endpoint, http, config.SpeedScale);
IAudioPlayer audio = new NAudioPlayer((float)config.PlaybackVolume);

const int zundamonSpeakerId = 3;
const string line = "こんにちは。ケイラボAIラジオへようこそ。ずんだもんが Windows でお送りするのだ。";

try
{
    log.Log($"合成中（speaker={zundamonSpeakerId}）: {line}");
    var wav = await tts.SynthesizeAsync(line, zundamonSpeakerId);
    log.Log($"合成完了: {wav.Length} bytes。再生します。");
    await audio.PlayAsync(wav);
    log.Log("再生完了。W1 OK。");
    return 0;
}
catch (RadioException ex)
{
    log.Log($"エラー [{ex.Code}]: {ex.Message}");
    return 1;
}
