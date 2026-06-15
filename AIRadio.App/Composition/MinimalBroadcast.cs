using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.App.Composition;

/// <summary>
/// W9 の最小放送 = OP テーマ 1 本（BGM 検索 → <see cref="ThemeSequencer"/>）。
/// 番組進行（複数コーナー・ローリング準備）は W7/W10。config は放送開始ごとに新規ロードする
/// （YAML 編集が再起動なしで反映、design §1）。
/// </summary>
internal sealed class MinimalBroadcast
{
    private const int ZundamonSpeakerId = 3;
    private const string BgmQuery = "YOASOBI";
    private const string Tagline = "ケイラボAIラジオ、放送開始です。";
    private const string Announcement =
        "今日も一日おつかれさまなのだ。作業のおともに、ゆったりとした音楽をお届けするのだ。" +
        "肩の力を抜いて、のんびり聴いてほしいのだ。";

    private readonly string _configDir;
    private readonly IRadioLog _log;

    public MinimalBroadcast(string configDir, IRadioLog log)
    {
        _configDir = configDir;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var spotifyPath = Path.Combine(_configDir, "spotify.local.yaml");
            if (!File.Exists(spotifyPath))
            {
                _log.Log($"spotify.local.yaml が見つかりません: {spotifyPath}");
                _log.Log("先に `-- spotify-auth` でログインし、client_id を設定してください。");
                return;
            }
            var spotifyConfig = SpotifyConfig.LoadFile(spotifyPath);
            var ttsConfig = TtsConfig.LoadFile(Path.Combine(_configDir, "tts.yaml"));

            var http = new HttpClientAdapter();
            var clock = new SystemClock();
            var store = new DpapiTokenStore();
            string[] scopes = { "user-read-playback-state", "user-modify-playback-state" };
            var auth = new SpotifyAuth(
                spotifyConfig.ClientId, spotifyConfig.RedirectUri, spotifyConfig.LoopbackPort,
                scopes, store, http, clock);
            var searcher = new SpotifyWebSearcher(auth, http, spotifyConfig.Market);
            var spotify = new WebApiSpotifyController(auth, http, preferredDeviceName: spotifyConfig.DeviceName);
            var tts = new VoicevoxTTS(ttsConfig.Endpoint, http, ttsConfig.SpeedScale);
            var audio = new NAudioPlayer((float)ttsConfig.PlaybackVolume);
            var sequencer = new ThemeSequencer(tts, audio, spotify, clock);

            _log.Log("放送開始: オープニングを準備中…");
            var results = await searcher.SearchAsync(BgmQuery, 5, ct).ConfigureAwait(false);
            var bgm = results.FirstOrDefault(t => t.IsPlayable) ?? results.FirstOrDefault();
            if (bgm is null)
            {
                _log.Log("BGM 用トラックが見つかりませんでした。放送を中止します。");
                return;
            }

            var theme = new ThemeConfig(
                Tagline: Tagline, TrackUri: bgm.Uri,
                IntroSeconds: 5, Volume: 80, DuckedVolume: 30, OutroSeconds: 5);
            _log.Log($"オープニング BGM: {bgm.Title} / {bgm.Artist}");
            await sequencer.RunAsync(theme, Announcement, ZundamonSpeakerId, ct).ConfigureAwait(false);
            _log.Log("オープニング完了。放送を終えます。");
        }
        catch (OperationCanceledException)
        {
            // 停止要求。BGM 再生中なら ThemeSequencer が PauseIgnoringCancellation 済み（完全静寂 §3-1）。
            _log.Log("停止しました（完全静寂）。");
        }
        catch (RadioException ex)
        {
            _log.Log($"エラー [{ex.Code}]: {ex.Message}");
        }
    }
}
