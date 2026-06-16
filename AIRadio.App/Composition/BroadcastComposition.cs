using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.App.Composition;

/// <summary>
/// 番組 1 本（OP → トーク → ニュース → ED）の composition root（W7）。Mac 版 `BroadcastWiring.makeBroadcastStack` 相当。
/// config を放送開始ごとに新規ロードし（YAML 即反映, design §1）、協調オブジェクトを組み立てて
/// <see cref="BroadcastEngine"/> を 1 回 <c>RunAsync</c> する。W9 の <c>MinimalBroadcast</c>（OP 1 本）を置換する。
///
/// ニュース seam（§3-4）: <c>NewsWeatherProvider</c> は Infrastructure にあり Core から参照できないため、
/// <c>ct =&gt; provider.AnnouncementAsync(ct)</c> をデリゲートとしてエンジンへ注入する。
/// </summary>
internal sealed class BroadcastComposition
{
    private readonly string _configDir;
    private readonly IRadioLog _log;

    public BroadcastComposition(string configDir, IRadioLog log)
    {
        _configDir = configDir;
        _log = log;
    }

    /// <param name="control">「ED で終了」操作ハンドル（トレイから渡す。null なら ED 終了なし）。</param>
    public async Task RunAsync(CancellationToken ct, BroadcastControl? control = null)
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

            // 設定を毎回新規ロード（fail-fast: 欠落は ConfigException、API キー欠落は LlmException）。
            var spotifyConfig = SpotifyConfig.LoadFile(spotifyPath);
            var ttsConfig = TtsConfig.LoadFile(Path.Combine(_configDir, "tts.yaml"));
            var llmConfig = LlmConfig.LoadFiles(
                Path.Combine(_configDir, "llm.yaml"), Path.Combine(_configDir, "llm.local.yaml"));
            var research = ResearchConfig.LoadFile(Path.Combine(_configDir, "research.yaml"));
            var djs = DjsConfig.LoadFile(Path.Combine(_configDir, "djs.yaml"));
            var corners = CornersConfig.LoadFile(Path.Combine(_configDir, "corners.yaml"));
            var blueprint = ProgramConfig.LoadFile(Path.Combine(_configDir, "program.yaml"));
            var themes = ThemesConfig.LoadFile(Path.Combine(_configDir, "themes.yaml"));

            // 番組の長さ: メニュー選択（%LOCALAPPDATA% 永続化）が優先、なければ program.yaml の既定値（w13 §5）。
            var length = new ProgramLengthStore().Read() ?? blueprint.DefaultLength;
            var plan = new ProgramPlan(blueprint, length);

            // 共有インフラ。
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
            var llm = new GeminiLLMBackend(llmConfig, http);

            // 協調オブジェクト（OP/news/ED 演出・トーク・ニュース原稿）。
            var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock);
            var cornerEngine = new CornerEngine(
                llm, tts, audio, searcher, spotify, clock,
                temperature: llmConfig.Temperature,
                onEvent: e => _log.Log(FormatCornerEvent(e)));
            var news = new NewsRssSource(research.NewsRssUrl, http, research.NewsMaxItems);
            var weather = new JmaWeatherSource(research.WeatherAreaCode, research.WeatherAreaName, http);
            // ニュースは LLM アナウンサー原稿（W11）。読み手（program.news.dj_id、なければ anchor）のペルソナを使う。
            // LLM 不調時は announcement_template に自己完結フォールバック（fail-tolerant）。
            var newsDjId = blueprint.NewsDjId ?? blueprint.AnchorDjId;
            var newsPersona = djs.FirstOrDefault(d => d.Id == newsDjId)?.Persona ?? "";
            var newsProvider = new LlmNewsScriptProvider(
                news, weather, llm, newsPersona, research.LlmScript, research.AnnouncementTemplate);
            var songPicker = new SongPicker(llm, searcher, llmConfig.Temperature);

            var engine = new BroadcastEngine(
                themeSequencer,
                cornerEngine,
                songPicker,
                newsAnnouncement: token => newsProvider.AnnouncementAsync(token),
                spotify,
                clock,
                onEvent: e => _log.Log(FormatBroadcastEvent(e)));

            var lengthLabel = plan.Length.IsEndless ? "エンドレス" : $"トーク{plan.Length.Corners}本";
            var countLabel = plan.TotalSegmentCount is int total ? $"・{total} セグメント" : "";
            _log.Log($"放送開始: 「{plan.Title}」（{lengthLabel}{countLabel}）");
            await engine.RunAsync(plan, themes, corners, djs, control, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止要求。各セグメントとエンジンが PauseIgnoringCancellation 済み（完全静寂 §3-1）。
            _log.Log("停止しました（完全静寂）。");
        }
        catch (RadioException ex)
        {
            // 設定不正（fail-fast）/ critical セグメント中止 等。
            _log.Log($"エラー [{ex.Code}]: {ex.Message}");
        }
    }

    private static string FormatBroadcastEvent(BroadcastEvent e) => e switch
    {
        BroadcastEvent.SegmentStarted x => $"▶ セグメント{x.Index}: {x.Kind} 開始",
        BroadcastEvent.SegmentFinished x => $"■ セグメント{x.Index}: {x.Kind} 完了",
        BroadcastEvent.SegmentFailed x => $"⚠ セグメント{x.Index}: {x.Kind} 失敗 [{x.Code}] {x.Detail}",
        BroadcastEvent.SongStarted x => $"  ♪ 冒頭曲: {Label(x.Track)}",
        BroadcastEvent.SongFinished x => $"  ♪ 冒頭曲 終了（検知: {x.Reason}）",
        BroadcastEvent.EndingRequested => "ED で終了を受け付けました（残りのコーナーを飛ばして ED へ）。",
        BroadcastEvent.BroadcastFinished => "番組を最後まで放送しました。",
        _ => "",
    };

    private static string FormatCornerEvent(CornerEvent e) => e switch
    {
        CornerEvent.ThemeSelected x => $"  テーマ: {x.Theme}",
        CornerEvent.SongPicked x => $"  締めの曲（プレフライト済み）: {Label(x.Track)}",
        CornerEvent.ScriptReady x => $"  台本生成完了: {x.LineCount} 行 / {x.TotalCharacters} 文字",
        CornerEvent.Line x => $"    {x.DialogueLine.DjId}: {x.DialogueLine.Text}",
        CornerEvent.SongStarted x => $"  ♪ 再生中: {Label(x.Track)}",
        CornerEvent.SongFinished x => $"  ♪ 曲終了（検知: {x.Reason}）",
        _ => "",
    };

    private static string Label(TrackInfo track)
        => track.Title.Length == 0 ? track.Uri : $"{track.Artist} / {track.Title}";
}
