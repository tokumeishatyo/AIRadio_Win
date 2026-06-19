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
        AquesTalk1Synthesizer? aquestalk = null;   // W-AQT: finally で Dispose（AqKanji2Koe Release ＋ 声種モジュール Free）。
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
            // ゲストプール（W14）。ファイルが無ければ空（ゲストコーナー無効時はそれでよい。あるのに壊れていれば fail-fast）。
            var guestsPath = Path.Combine(_configDir, "guests.yaml");
            var guests = File.Exists(guestsPath) ? GuestsConfig.LoadFile(guestsPath) : Array.Empty<DjProfile>();
            // アーティスト特集プール（W15）。出荷時空・未生成は空＝実行時スキップ（あるのに壊れていれば fail-fast）。
            var artists = ArtistsConfig.LoadFile(Path.Combine(_configDir, "artists.yaml"));
            // 暦コンテキスト（W17。曜日名・記念日）。ファイルが無ければ Standard（曜日名のみ・記念日なし。あるのに壊れていれば fail-fast）。
            var calendarPath = Path.Combine(_configDir, "calendar.yaml");
            var calendar = File.Exists(calendarPath) ? DailyCalendarConfig.LoadFile(calendarPath) : DailyCalendar.Standard;
            // 読み辞書（W19a）。無ければ空＝同期なし。壊れていれば fail-fast（ConfigException）。W19a は pronunciations.yaml のみ（artists の reading 統合は W19b）。
            var pronunciations = PronunciationsConfig.LoadFile(Path.Combine(_configDir, "pronunciations.yaml"));

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
            var catalog = new SpotifyArtistCatalog(auth, http, spotifyConfig.Market);
            var spotify = new WebApiSpotifyController(auth, http, preferredDeviceName: spotifyConfig.DeviceName);
            // TTS: VOICEVOX を基本に、djs/guests.yaml の tts_backend: aquestalk な DJ を AquesTalk1 へ振り分ける（W-AQT）。
            // speaker_id マップは djs ∪ guests から構築（一意性違反は ConfigException＝fail-fast・WAQT-3）。
            var voiceMap = RoutingTTS.BuildVoiceMap(djs.Concat(guests));
            aquestalk = voiceMap.Count > 0 ? TryBuildAquesTalk(ttsConfig) : null;
            var tts = new RoutingTTS(
                new VoicevoxTTS(ttsConfig.Endpoint, http, ttsConfig.SpeedScale),
                aquestalk, voiceMap, _log.Log);
            // 読み辞書の同期は VOICEVOX と同じ endpoint・http を共有（W19a §7）。AquesTalk は対象外（VOICEVOX 専用辞書）。
            var userDict = new VoicevoxUserDict(ttsConfig.Endpoint, http);
            var audio = new NAudioPlayer((float)ttsConfig.PlaybackVolume);
            var llm = new GeminiLLMBackend(llmConfig, http);

            // 協調オブジェクト（OP/news/ED 演出・トーク・ニュース原稿）。warn は多声 OP の同時発話ミックス形式不一致の通知（W-OP）。
            var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock, _log.Log);
            var cornerEngine = new CornerEngine(
                llm, tts, audio, searcher, spotify, clock,
                temperature: llmConfig.Temperature,
                onEvent: e => _log.Log(FormatCornerEvent(e)),
                calendar: calendar);
            // アーティスト特集ランナー（W15）。onEvent を log へブリッジしないと FeatureSkipped（ART コード）が無出力になる（§18-32）。
            var artistFeatureEngine = new ArtistFeatureEngine(
                llm, tts, audio, catalog, spotify, clock,
                temperature: llmConfig.Temperature,
                onEvent: e => _log.Log(FormatArtistFeatureEvent(e)),
                calendar: calendar);
            var news = new NewsRssSource(research.NewsRssUrl, http, research.NewsMaxItems);
            var weather = new JmaWeatherSource(research.WeatherAreaCode, research.WeatherAreaName, http);
            // ニュースは LLM アナウンサー原稿（W11）。読み手（program.news.dj_id、なければ anchor）のペルソナを使う。
            // LLM 不調時は announcement_template に自己完結フォールバック（fail-tolerant）。
            var newsDjId = blueprint.NewsDjId ?? blueprint.AnchorDjId;
            var newsPersona = djs.FirstOrDefault(d => d.Id == newsDjId)?.Persona ?? "";
            var newsProvider = new LlmNewsScriptProvider(
                news, weather, llm, newsPersona, research.LlmScript, research.AnnouncementTemplate);
            var songPicker = new SongPicker(llm, searcher, llmConfig.Temperature);
            // 長期記憶（W18）。journal.local.yaml（gitignore・人為削除で即クリア）。要約は temp 0.6（Mac 一致）。
            var journalStore = new YamlJournalStore(Path.Combine(_configDir, "journal.local.yaml"));
            var journalSummarizer = new JournalSummarizer(llm);
            // 掛け合い指示（W-DLG）。banter.yaml（任意・不在なら Empty＝注入なし）。
            var banterDirectives = BanterConfig.LoadFile(Path.Combine(_configDir, "banter.yaml"));

            var engine = new BroadcastEngine(
                themeSequencer,
                cornerEngine,
                songPicker,
                newsAnnouncement: token => newsProvider.AnnouncementAsync(token),
                spotify,
                clock,
                onEvent: e => _log.Log(FormatBroadcastEvent(e)),
                artistFeatureRunner: artistFeatureEngine,
                journalStore: journalStore,
                journalSummarizer: journalSummarizer,
                banterDirectives: banterDirectives);

            var lengthLabel = plan.Length.IsEndless ? "エンドレス" : $"トーク{plan.Length.Corners}本";
            var countLabel = plan.TotalSegmentCount is int total ? $"・{total} セグメント" : "";
            _log.Log($"放送開始: 「{plan.Title}」（{lengthLabel}{countLabel}）");
            // 放送開始時に読み辞書を冪等同期（fail-tolerant・throw しない。W19a §5/§7）。本番・broadcast デモ両経路をカバー。
            // pronunciations.yaml（明示・優先）＋ artists.yaml の reading（W19b）を統合して同期する。
            var pronEntries = VoicevoxUserDict.MergedEntries(pronunciations, artists);
            LogPronunciationSync(await userDict.SyncAsync(pronEntries, ct).ConfigureAwait(false));
            await engine.RunAsync(plan, themes, corners, djs, control, guests, artists, ct).ConfigureAwait(false);
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
        finally
        {
            // AquesTalk リソース解放（AqKanji2Koe Release ＋ 声種モジュール Free。W-AQT）。
            aquestalk?.Dispose();
        }
    }

    /// <summary>
    /// AquesTalk 合成器を構築する（W-AQT）。DLL/辞書不在・初期化失敗は <c>null</c>（当該 DJ 無音・放送継続）。
    /// dev key 未設定は WARN（T04・評価版でナ/マ行→ヌ）。所有権は呼び出し側（<see cref="RunAsync"/> の finally で Dispose）。
    /// </summary>
    private AquesTalk1Synthesizer? TryBuildAquesTalk(TtsConfig ttsConfig)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var keys = AquesTalkLocalConfig.LoadFile(Path.Combine(_configDir, "aquestalk.local.yaml"));
            if (string.IsNullOrEmpty(keys.AquesTalkDevKey) || string.IsNullOrEmpty(keys.AqKanji2KoeDevKey))
            {
                _log.Log("⚠ AquesTalk 評価版動作: dev key 未設定でナ/マ行が「ヌ」化します（config/aquestalk.local.yaml）。");
            }
            var native = new AquesTalk1Native(
                Path.Combine(baseDir, ttsConfig.AquesTalkVoicesDir),
                Path.Combine(baseDir, ttsConfig.AquesTalkDictDir),
                keys.AquesTalkDevKey, keys.AquesTalkUsrKey);
            return new AquesTalk1Synthesizer(
                native,
                Path.Combine(baseDir, ttsConfig.AquesTalkDictDir, "aq_dic"),
                keys.AqKanji2KoeDevKey, ttsConfig.AquesTalkSpeed);
        }
        catch (Exception ex)
        {
            // DLL/辞書不在・初期化失敗 → 当該 DJ は無音で継続（fail-tolerant・spec §5-3）。
            _log.Log($"⚠ AquesTalk エンジンの初期化に失敗しました（該当 DJ は無音で継続）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 「アーティスト一覧を生成」（W15 §9-3）。<c>llm.yaml</c> + <c>spotify.local.yaml</c> をロードして LLM + <see cref="IArtistCatalog"/> を
    /// 組み立て、<c>artist-gen.yaml</c> の指定で <c>artists.yaml</c> を原子的に上書きする（オフライン・音/再生デバイス不要）。
    /// 返り値は確定組数。失敗・0 組は throw（呼び出し側でファイル不変＝<see cref="ArtistListGenerator.Write"/> 前に throw）。
    /// </summary>
    public async Task<int> GenerateArtistListAsync(CancellationToken ct = default)
    {
        var spotifyPath = Path.Combine(_configDir, "spotify.local.yaml");
        if (!File.Exists(spotifyPath))
        {
            throw ConfigException.MissingField("spotify.local.yaml（アーティスト生成には Spotify 認証が必要です）");
        }
        var spotifyConfig = SpotifyConfig.LoadFile(spotifyPath);
        var llmConfig = LlmConfig.LoadFiles(
            Path.Combine(_configDir, "llm.yaml"), Path.Combine(_configDir, "llm.local.yaml"));
        var genConfig = ArtistGenConfig.LoadFile(Path.Combine(_configDir, "artist-gen.yaml"));

        var http = new HttpClientAdapter();
        var clock = new SystemClock();
        var store = new DpapiTokenStore();
        string[] scopes = { "user-read-playback-state", "user-modify-playback-state" };
        var auth = new SpotifyAuth(
            spotifyConfig.ClientId, spotifyConfig.RedirectUri, spotifyConfig.LoopbackPort,
            scopes, store, http, clock);
        var catalog = new SpotifyArtistCatalog(auth, http, spotifyConfig.Market);
        var llm = new GeminiLLMBackend(llmConfig, http);

        var generator = new ArtistListGenerator(llm, catalog);
        var artistsPath = Path.Combine(_configDir, "artists.yaml");
        _log.Log($"アーティスト一覧を生成します（{genConfig.GenrePrompt} / 目標 {genConfig.TargetCount} 組）…");
        var count = await generator.GenerateAsync(genConfig, artistsPath, ct: ct).ConfigureAwait(false);
        _log.Log($"アーティスト一覧を生成しました（{count} 組）→ {artistsPath}");
        return count;
    }

    // 読み辞書同期（W19a）の PRON コード。診断ログ用・throw しない（Mac は logPronunciationSync にリテラル直書き＝App 層）。
    // Core に定数クラスを置かない（PRON コードの消費者は App のログ整形だけ。§3-5 / spec §8）。
    private const string PronSyncUnreachableCode = "E-PRON-SYNC-UNREACHABLE-001";
    private const string PronWordRejectedCode = "E-PRON-WORD-REJECTED-001";

    /// <summary>
    /// 読み辞書同期（W19a）の結果を進行ログに出す（診断用。コードは error-codes.md の PRON カテゴリ）。
    /// 接続不可は専用行、変更ありのときだけ件数行、変更なし（skip のみ）は無言（Mac <c>logPronunciationSync</c> 相当）。
    /// </summary>
    private void LogPronunciationSync(PronunciationSyncSummary summary)
    {
        if (summary.Unreachable)
        {
            _log.Log($"  📖 読み辞書: VOICEVOX に接続できず同期スキップ [{PronSyncUnreachableCode}]");
            return;
        }
        if (summary.Added + summary.Updated + summary.Failed == 0)
        {
            return;   // 変更なし（skip のみ）は無言。
        }
        var line = $"  📖 読み辞書同期: 追加{summary.Added} 更新{summary.Updated} skip{summary.Skipped}";
        if (summary.Failed > 0)
        {
            line += $" 失敗{summary.Failed} [{PronWordRejectedCode}]";
        }
        _log.Log(line);
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

    /// <summary>アーティスト特集（W15）のイベントを log 表示へ。FeatureSkipped は ART コード入り reason をそのまま出す（§11/§15）。</summary>
    private static string FormatArtistFeatureEvent(ArtistFeatureEvent e) => e switch
    {
        ArtistFeatureEvent.ArtistSelected x => $"  特集アーティスト: {x.Name}",
        ArtistFeatureEvent.TracksPrepared x => $"  特集曲数（重複除外後）: {x.Count} 曲",
        ArtistFeatureEvent.PartScriptReady x => $"  特集パート台本: {x.LineCount} 行 / {x.TotalCharacters} 文字",
        ArtistFeatureEvent.LeadIn x => $"  特集リード文: {x.Text}",
        ArtistFeatureEvent.Line x => $"    {x.DialogueLine.DjId}: {x.DialogueLine.Text}",
        ArtistFeatureEvent.SongStarted x => $"  ♪ 特集再生中: {Label(x.Track)}",
        ArtistFeatureEvent.SongFinished x => $"  ♪ 特集曲終了（検知: {x.Reason}）",
        ArtistFeatureEvent.FeatureSkipped x => $"  特集スキップ: {x.Reason}",
        _ => "",
    };

    private static string Label(TrackInfo track)
        => track.Title.Length == 0 ? track.Uri : $"{track.Artist} / {track.Title}";
}
