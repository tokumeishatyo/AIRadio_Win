using AIRadio.App.Composition;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.App;

// ケイラボAIラジオ (Windows) — CLI デモランナー（W1-W4 の検証用。常駐 UI は引数なしで W9）。
//   tts          : VOICEVOX で 1 行合成 → NAudio 再生（W1）
//   spotify-auth : ブラウザで Spotify PKCE ログイン → refresh を DPAPI 保管（W2）
//   spotify      : 検索 → プレフライト（isPlayable）表示（W2）
//   spotify-play : 検索 → 先頭曲を再生 → 数秒後に停止（W3。要 Premium + アクティブデバイス）
//   theme        : BGM ダッキング演出（tagline→BGM→duck→発話→outro→停止）（W4。要 VOICEVOX + Premium）
//   news         : ニュース見出し + 天気を取得して定型原稿を生成・表示（W5。fail-tolerant、音声なし）
//   corner       : Gemini 台本 → 2-DJ 会話 → 締めに一曲（W6。要 Gemini キー + VOICEVOX + Premium）
//   broadcast    : 番組 1 本（OP → トーク → ニュース → ED）を通し再生（W7。Ctrl-C で完全静寂）
internal static class DemoRunner
{
    private static readonly IRadioLog Log = new ConsoleRadioLog();
    private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config");

    public static async Task<int> RunAsync(string[] args)
    {
        var mode = args[0];
        return mode switch
        {
            "tts" => await RunTtsDemoAsync(),
            "spotify-auth" => await RunSpotifyAuthAsync(),
            "spotify" => await RunSpotifySearchAsync(),
            "spotify-play" => await RunSpotifyPlayAsync(),
            "theme" => await RunThemeDemoAsync(),
            "news" => await RunNewsDemoAsync(),
            "corner" => await RunCornerDemoAsync(),
            "broadcast" => await RunBroadcastDemoAsync(),
            _ => Unknown(mode),
        };
    }

    private static int Unknown(string mode)
    {
        Log.Log($"不明なモード: {mode}（tts / spotify-auth / spotify / spotify-play / theme / news / corner / broadcast）");
        return 2;
    }

    private static async Task<int> RunBroadcastDemoAsync()
    {
        Log.Log("ケイラボAIラジオ (Windows) — W7 番組進行デモ（OP → トーク → ニュース → ED）");
        Log.Log("（停止するには Ctrl-C。完全静寂になることを確認できます）");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // プロセスを即殺さず CTS 経由で graceful に止める（後始末 pause を送り切る）。
            cts.Cancel();
        };

        // BroadcastComposition が config 新規ロード → エンジン組み立て → 1 本実行。
        // 設定不正・critical 中止・キャンセルはすべて内部でログ + 静寂化する（例外は外へ出さない）。
        var composition = new BroadcastComposition(ConfigDir, Log);
        await composition.RunAsync(cts.Token);
        Log.Log("W7 番組進行デモ終了。");
        return 0;
    }

    private static async Task<int> RunTtsDemoAsync()
    {
        Log.Log("ケイラボAIラジオ (Windows) — W1 VOICEVOX TTS + 音声再生デモ");
        var configPath = Path.Combine(ConfigDir, "tts.yaml");
        TtsConfig config;
        try
        {
            config = TtsConfig.LoadFile(configPath);
        }
        catch (ConfigException ex)
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}（{configPath}）");
            return 1;
        }

        Log.Log($"VOICEVOX endpoint={config.Endpoint} / speed_scale={config.SpeedScale} / volume={config.PlaybackVolume}");
        IHttpClient http = new HttpClientAdapter();
        ITTSBackend tts = new VoicevoxTTS(config.Endpoint, http, config.SpeedScale);
        IAudioPlayer audio = new NAudioPlayer((float)config.PlaybackVolume);

        const int zundamonSpeakerId = 3;
        const string line = "こんにちは。ケイラボAIラジオへようこそ。ずんだもんが Windows でお送りするのだ。";
        try
        {
            Log.Log($"合成中（speaker={zundamonSpeakerId}）: {line}");
            var wav = await tts.SynthesizeAsync(line, zundamonSpeakerId);
            Log.Log($"合成完了: {wav.Length} bytes。再生します。");
            await audio.PlayAsync(wav);
            Log.Log("再生完了。W1 OK。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSpotifyAuthAsync()
    {
        var cfg = TryLoadSpotify();
        if (cfg is null)
        {
            return 1;
        }
        var auth = MakeAuth(cfg);
        try
        {
            Log.Log("ブラウザで Spotify にログインしてください…");
            await auth.AuthorizeAsync();
            Log.Log("ログイン成功。refresh トークンを DPAPI で保存しました。W2 認証 OK。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSpotifySearchAsync()
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
            Log.Log($"検索: {query}（market={cfg.Market}）");
            var results = await searcher.SearchAsync(query, 5);
            foreach (var track in results)
            {
                var playable = await searcher.IsPlayableAsync(track.Uri);
                Log.Log($"  {(playable ? "✓" : "✗")} {track.Title} / {track.Artist}  {track.Uri}");
            }
            Log.Log("検索・プレフライトデモ完了。W2 OK。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSpotifyPlayAsync()
    {
        var cfg = TryLoadSpotify();
        if (cfg is null)
        {
            return 1;
        }
        var auth = MakeAuth(cfg);
        IHttpClient http = new HttpClientAdapter();
        var searcher = new SpotifyWebSearcher(auth, http, cfg.Market);
        var spotify = new WebApiSpotifyController(auth, http, preferredDeviceName: cfg.DeviceName);

        const string query = "YOASOBI";
        const int playSeconds = 8;
        try
        {
            Log.Log($"検索: {query}（market={cfg.Market}）");
            var results = await searcher.SearchAsync(query, 5);
            var track = results.FirstOrDefault(t => t.IsPlayable) ?? results.FirstOrDefault();
            if (track is null)
            {
                Log.Log("再生可能な曲が見つかりませんでした。");
                return 1;
            }

            Log.Log($"再生: {track.Title} / {track.Artist}  {track.Uri}");
            await spotify.PlayAsync(track.Uri);
            var state = await spotify.PlayerStateAsync();
            Log.Log($"プレイヤー状態: {state.State} pos={state.PositionSeconds:0.0}s / dur={state.DurationSeconds:0.0}s");

            Log.Log($"{playSeconds} 秒再生して停止します…");
            await Task.Delay(TimeSpan.FromSeconds(playSeconds));
            await spotify.PauseAsync();
            Log.Log("停止しました。W3 再生デモ完了。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunThemeDemoAsync()
    {
        var cfg = TryLoadSpotify();
        if (cfg is null)
        {
            return 1;
        }
        var ttsConfigPath = Path.Combine(ConfigDir, "tts.yaml");
        TtsConfig ttsConfig;
        try
        {
            ttsConfig = TtsConfig.LoadFile(ttsConfigPath);
        }
        catch (ConfigException ex)
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}（{ttsConfigPath}）");
            return 1;
        }

        var auth = MakeAuth(cfg);
        IHttpClient http = new HttpClientAdapter();
        var searcher = new SpotifyWebSearcher(auth, http, cfg.Market);
        ITTSBackend tts = new VoicevoxTTS(ttsConfig.Endpoint, http, ttsConfig.SpeedScale);
        IAudioPlayer audio = new NAudioPlayer((float)ttsConfig.PlaybackVolume);
        var spotify = new WebApiSpotifyController(auth, http, preferredDeviceName: cfg.DeviceName);
        var sequencer = new ThemeSequencer(tts, audio, spotify, new SystemClock());

        const int zundamonSpeakerId = 3;
        const string announcement =
            "今日も一日おつかれさまなのだ。作業のおともに、ゆったりとした音楽をお届けするのだ。" +
            "肩の力を抜いて、のんびり聴いてほしいのだ。";
        try
        {
            Log.Log("BGM 用トラックを検索します…");
            var results = await searcher.SearchAsync("YOASOBI", 5);
            var bgm = results.FirstOrDefault(t => t.IsPlayable) ?? results.FirstOrDefault();
            if (bgm is null)
            {
                Log.Log("BGM 用トラックが見つかりませんでした。");
                return 1;
            }

            var theme = new ThemeConfig(
                Tagline: "ケイラボAIラジオ、オープニングです。",
                TrackUri: bgm.Uri,
                IntroSeconds: 5,
                Volume: 80,
                DuckedVolume: 30,
                OutroSeconds: 5);

            Log.Log($"BGM: {bgm.Title} / {bgm.Artist}  {bgm.Uri}");
            Log.Log("テーマ演出（tagline → BGM → ダッキング → 発話 → アウトロ → 停止）を開始します…");
            await sequencer.RunAsync(theme, announcement, zundamonSpeakerId);
            Log.Log("テーマ演出完了。W4 OK。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunNewsDemoAsync()
    {
        Log.Log("ケイラボAIラジオ (Windows) — W5 ニュース・天気デモ（fail-tolerant、音声なし）");
        var configPath = Path.Combine(ConfigDir, "research.yaml");
        ResearchConfig config;
        try
        {
            config = ResearchConfig.LoadFile(configPath);
        }
        catch (ConfigException ex)
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}（{configPath}）");
            return 1;
        }

        IHttpClient http = new HttpClientAdapter();
        var news = new NewsRssSource(config.NewsRssUrl, http, config.NewsMaxItems);
        var weather = new JmaWeatherSource(config.WeatherAreaCode, config.WeatherAreaName, http);
        var provider = new NewsWeatherProvider(news, weather, config.AnnouncementTemplate);

        Log.Log($"取得中…（RSS={config.NewsRssUrl} / 天気={config.WeatherAreaName}[{config.WeatherAreaCode}]）");
        var announcement = await provider.AnnouncementAsync();
        Log.Log("--- ニュース原稿 ---");
        Log.Log(announcement);
        Log.Log("W5 OK（取得失敗時はフォールバック文言）。");
        return 0;
    }

    private static async Task<int> RunCornerDemoAsync()
    {
        Log.Log("ケイラボAIラジオ (Windows) — W6 トークコーナー デモ（Gemini 台本 → 2-DJ 会話 → 一曲）");

        LlmConfig llmConfig;
        try
        {
            llmConfig = LlmConfig.LoadFiles(
                Path.Combine(ConfigDir, "llm.yaml"), Path.Combine(ConfigDir, "llm.local.yaml"));
        }
        catch (RadioException ex) // ConfigException（model 欠落）/ LlmException（キー欠落）
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }

        var cfg = TryLoadSpotify();
        if (cfg is null)
        {
            return 1;
        }

        TtsConfig ttsConfig;
        IReadOnlyList<DjProfile> djs;
        IReadOnlyList<CornerTemplate> corners;
        try
        {
            ttsConfig = TtsConfig.LoadFile(Path.Combine(ConfigDir, "tts.yaml"));
            djs = DjsConfig.LoadFile(Path.Combine(ConfigDir, "djs.yaml"));
            corners = CornersConfig.LoadFile(Path.Combine(ConfigDir, "corners.yaml"));
        }
        catch (ConfigException ex)
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }

        var corner = corners.FirstOrDefault(c => c.Format == CornerFormat.FreeTalk);
        if (corner is null)
        {
            Log.Log("free_talk コーナーが corners.yaml にありません。");
            return 1;
        }

        IHttpClient http = new HttpClientAdapter();
        var auth = MakeAuth(cfg);
        var llm = new GeminiLLMBackend(llmConfig, http);
        ITTSBackend tts = new VoicevoxTTS(ttsConfig.Endpoint, http, ttsConfig.SpeedScale);
        IAudioPlayer audio = new NAudioPlayer((float)ttsConfig.PlaybackVolume);
        var searcher = new SpotifyWebSearcher(auth, http, cfg.Market);
        var spotify = new WebApiSpotifyController(auth, http, preferredDeviceName: cfg.DeviceName);
        var engine = new CornerEngine(
            llm, tts, audio, searcher, spotify, new SystemClock(),
            temperature: llmConfig.Temperature,
            onEvent: e => Log.Log(FormatCornerEvent(e)));

        try
        {
            Log.Log($"コーナー「{corner.Title}」（テーマ: {corner.Theme}）を開始します…");
            await engine.RunAsync(corner, djs);
            Log.Log("コーナー完了。W6 OK。");
            return 0;
        }
        catch (RadioException ex)
        {
            Log.Log($"エラー [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }

    private static string FormatCornerEvent(CornerEvent e) => e switch
    {
        CornerEvent.ThemeSelected x => $"テーマ: {x.Theme}",
        CornerEvent.SongPicked x => $"締めの曲（プレフライト済み）: {Label(x.Track)}",
        CornerEvent.ScriptReady x => $"台本生成完了: {x.LineCount} 行 / {x.TotalCharacters} 文字",
        CornerEvent.Line x => $"  {x.DialogueLine.DjId}: {x.DialogueLine.Text}",
        CornerEvent.SongStarted x => $"♪ 再生中: {Label(x.Track)}",
        CornerEvent.SongFinished x => $"♪ 曲終了（検知: {x.Reason}）",
        _ => "",
    };

    private static string Label(TrackInfo track)
        => track.Title.Length == 0 ? track.Uri : $"{track.Artist} / {track.Title}";

    private static SpotifyConfig? TryLoadSpotify()
    {
        var path = Path.Combine(ConfigDir, "spotify.local.yaml");
        if (!File.Exists(path))
        {
            Log.Log($"spotify.local.yaml が見つかりません: {path}");
            Log.Log("config/spotify.local.yaml.sample をコピーして client_id を記入してください。");
            return null;
        }
        try
        {
            return SpotifyConfig.LoadFile(path);
        }
        catch (ConfigException ex)
        {
            Log.Log($"設定エラー [{ex.Code}]: {ex.Message}");
            return null;
        }
    }

    private static SpotifyAuth MakeAuth(SpotifyConfig cfg)
    {
        IHttpClient http = new HttpClientAdapter();
        ITokenStore store = new DpapiTokenStore();
        string[] scopes = { "user-read-playback-state", "user-modify-playback-state" };
        return new SpotifyAuth(cfg.ClientId, cfg.RedirectUri, cfg.LoopbackPort, scopes, store, http, new SystemClock());
    }
}
