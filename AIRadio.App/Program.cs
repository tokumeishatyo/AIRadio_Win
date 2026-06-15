using AIRadio.Core;
using AIRadio.Infrastructure;

// ケイラボAIラジオ (Windows) — デモランナー（トレイ常駐 UI は W9）。
//   (引数なし) / tts   : VOICEVOX で 1 行合成 → NAudio 再生（W1）
//   spotify-auth        : ブラウザで Spotify PKCE ログイン → refresh を DPAPI 保管（W2）
//   spotify             : 検索 → プレフライト（isPlayable）表示（W2）
// Debug ビルドはコンソールに全ログがストリームする（Mac 版のコンソール起動を踏襲）。

IRadioLog log = new ConsoleRadioLog();
var configDir = Path.Combine(AppContext.BaseDirectory, "config");
var mode = args.Length > 0 ? args[0] : "tts";

switch (mode)
{
    case "tts":
        return await RunTtsDemoAsync();
    case "spotify-auth":
        return await RunSpotifyAuthAsync();
    case "spotify":
        return await RunSpotifySearchAsync();
    default:
        log.Log($"不明なモード: {mode}（tts / spotify-auth / spotify）");
        return 2;
}

async Task<int> RunTtsDemoAsync()
{
    log.Log("ケイラボAIラジオ (Windows) — W1 VOICEVOX TTS + 音声再生デモ");
    var configPath = Path.Combine(configDir, "tts.yaml");
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
}

async Task<int> RunSpotifyAuthAsync()
{
    var cfg = TryLoadSpotify();
    if (cfg is null)
    {
        return 1;
    }
    var auth = MakeAuth(cfg);
    try
    {
        log.Log("ブラウザで Spotify にログインしてください…");
        await auth.AuthorizeAsync();
        log.Log("ログイン成功。refresh トークンを DPAPI で保存しました。W2 認証 OK。");
        return 0;
    }
    catch (RadioException ex)
    {
        log.Log($"エラー [{ex.Code}]: {ex.Message}");
        return 1;
    }
}

async Task<int> RunSpotifySearchAsync()
{
    var cfg = TryLoadSpotify();
    if (cfg is null)
    {
        return 1;
    }
    var auth = MakeAuth(cfg);
    IHttpClient http = new HttpClientAdapter();
    var searcher = new SpotifyWebSearcher(auth, http, cfg.Market);

    const string query = "YOASOBI";
    try
    {
        log.Log($"検索: {query}（market={cfg.Market}）");
        var results = await searcher.SearchAsync(query, 5);
        foreach (var track in results)
        {
            var playable = await searcher.IsPlayableAsync(track.Uri);
            log.Log($"  {(playable ? "✓" : "✗")} {track.Title} / {track.Artist}  {track.Uri}");
        }
        log.Log("検索・プレフライトデモ完了。W2 OK。");
        return 0;
    }
    catch (RadioException ex)
    {
        log.Log($"エラー [{ex.Code}]: {ex.Message}");
        return 1;
    }
}

SpotifyConfig? TryLoadSpotify()
{
    var path = Path.Combine(configDir, "spotify.local.yaml");
    if (!File.Exists(path))
    {
        log.Log($"spotify.local.yaml が見つかりません: {path}");
        log.Log("config/spotify.local.yaml.sample をコピーして client_id を記入してください。");
        return null;
    }
    try
    {
        return SpotifyConfig.LoadFile(path);
    }
    catch (ConfigException ex)
    {
        log.Log($"設定エラー [{ex.Code}]: {ex.Message}");
        return null;
    }
}

SpotifyAuth MakeAuth(SpotifyConfig cfg)
{
    IHttpClient http = new HttpClientAdapter();
    ITokenStore store = new DpapiTokenStore();
    string[] scopes = { "user-read-playback-state", "user-modify-playback-state" };
    return new SpotifyAuth(cfg.ClientId, cfg.RedirectUri, cfg.LoopbackPort, scopes, store, http, new SystemClock());
}
