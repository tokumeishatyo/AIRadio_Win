using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

public class CornerEngineTests
{
    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    private static CornerTemplate FreeTalkCorner(int playSeconds = 30) => new(
        "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
        new[] { "zundamon", "metan" }, "spotify:track:fallback",
        Volume: 100, PlaySeconds: playSeconds);

    private const string ScriptResponse =
        "ずんだもん: こんにちは、なのだ。\n" +
        "四国めたん: どうも。\n" +
        "ずんだもん: 今日のテーマは音楽なのだ。\n" +
        "四国めたん: いいですわね。";

    [Fact]
    public async Task RunAsync_FreeTalk_PicksSong_SpeaksScript_PlaysSong_ThenPauses()
    {
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var spotify = new FakeSpotifyController();
        var audio = new SpyAudioPlayer();
        var events = new List<CornerEvent>();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), audio, searcher, spotify, new FakeClock(),
            onEvent: e => { lock (events) { events.Add(e); } });

        await engine.RunAsync(FreeTalkCorner(), Djs);

        Assert.Equal(4, audio.Played.Count); // 4 行発話
        // 行ごとに正しい話者へルーティングされている（InMemoryTTS は "{speakerId}:{text}" を返す）。
        // 台本は ずんだもん(3) / 四国めたん(2) の交互。
        var played = audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.StartsWith("3:", played[0]); // ずんだもん → speaker 3
        Assert.StartsWith("2:", played[1]); // 四国めたん → speaker 2
        Assert.StartsWith("3:", played[2]);
        Assert.StartsWith("2:", played[3]);
        Assert.Contains(new SpotifyEvent.Play("spotify:track:idol"), spotify.Events);
        Assert.Contains(new SpotifyEvent.SetVolume(100), spotify.Events);
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last()); // 完全静寂

        lock (events)
        {
            Assert.Contains(events, e => e is CornerEvent.SongPicked sp && sp.Track.Uri == "spotify:track:idol");
            Assert.Contains(events, e => e is CornerEvent.ScriptReady sr && sr.LineCount == 4);
            Assert.Equal(4, events.Count(e => e is CornerEvent.Line));
        }
    }

    [Theory]
    [InlineData(CornerFormat.Guest)]        // W14
    [InlineData(CornerFormat.ArtistFeature)] // W15
    public async Task PrepareAsync_UnsupportedFormat_ThrowsConfigException(CornerFormat format)
    {
        // letter は W12 で対応済み。guest / artist_feature は引き続き未対応（同一ガード）。
        var engine = new CornerEngine(
            new ScriptedLLM(), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeTrackSearcher(), new FakeSpotifyController(), new FakeClock());
        var corner = new CornerTemplate(
            "x", "X", "x", format, new[] { "zundamon" }, "spotify:track:fb");

        await Assert.ThrowsAsync<ConfigException>(() => engine.PrepareAsync(corner, Djs));
    }

    [Fact]
    public async Task PrepareAsync_ThemePool_SelectsViaInjectedRandom()
    {
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        var events = new List<CornerEvent>();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), new SpyAudioPlayer(), searcher, new FakeSpotifyController(), new FakeClock(),
            onEvent: e => { lock (events) { events.Add(e); } },
            randomIndex: _ => 1); // プールの index 1 を決定論的に選ぶ
        var corner = FreeTalkCorner() with { ThemePool = new[] { "お酒", "旅行", "音楽" } };

        await engine.PrepareAsync(corner, Djs);

        Assert.Contains(events, e => e is CornerEvent.ThemeSelected ts && ts.Theme == "旅行"); // index 1
    }

    [Fact]
    public async Task PrepareAsync_EmptyThemePool_UsesFixedTheme()
    {
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        var events = new List<CornerEvent>();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), new SpyAudioPlayer(), searcher, new FakeSpotifyController(), new FakeClock(),
            onEvent: e => { lock (events) { events.Add(e); } });

        await engine.PrepareAsync(FreeTalkCorner(), Djs); // ThemePool null → corner.Theme "音楽"

        Assert.Contains(events, e => e is CornerEvent.ThemeSelected ts && ts.Theme == "音楽");
    }

    [Fact]
    public async Task RunAsync_Letter_GeneratesLetter_PicksSongFromLetterBody_BuildsScript()
    {
        // LLM 呼び出し順: ①お便り → ②リクエスト曲候補 → ③台本（letter 3 段）。
        var llm = new ScriptedLLM(
            "ずんだ太郎\n最近旅行に行って楽しかったです。",  // ①お便り
            "アイドル - YOASOBI",                          // ②曲候補
            ScriptResponse);                               // ③台本
        var searcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        var spotify = new FakeSpotifyController();
        var audio = new SpyAudioPlayer();
        var events = new List<CornerEvent>();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), audio, searcher, spotify, new FakeClock(),
            onEvent: e => { lock (events) { events.Add(e); } });
        var corner = new CornerTemplate(
            "letter", "お便りのコーナー", "旅行", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:fb", Volume: 100, PlaySeconds: 5);

        await engine.RunAsync(corner, Djs);

        // ① お便り生成イベント（ラジオネーム）。
        Assert.Contains(events, e => e is CornerEvent.LetterReady lr && lr.RadioName == "ずんだ太郎");
        // ② 選曲コンテキストにお便り本文が乗る（SongPicker への LLM プロンプト）。
        Assert.Contains(llm.Requests, r => r.Prompt.Contains("お便りの内容: 最近旅行に行って楽しかったです。"));
        // ③ 台本プロンプトにお便り節（ラジオネーム + 本文）。
        Assert.Contains(llm.Requests, r => r.Prompt.Contains("# リスナーからのお便り") && r.Prompt.Contains("ずんだ太郎"));
        // 台本が発話され、最後は完全静寂。
        Assert.Equal(4, audio.Played.Count);
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last());
    }

    [Fact]
    public async Task PrepareAsync_UnknownDj_ThrowsConfigException()
    {
        var engine = new CornerEngine(
            new ScriptedLLM("アイドル - YOASOBI", ScriptResponse), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI") }),
            new FakeSpotifyController(), new FakeClock());
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "unknown_dj" }, "spotify:track:fb");

        await Assert.ThrowsAsync<ConfigException>(() => engine.PrepareAsync(corner, Djs));
    }

    [Fact]
    public async Task RunAsync_PausesEvenWhenPlaybackFails()
    {
        // prepare は audio を使わない（tts のみ）→ 成功。run の発話再生で失敗 → 完全静寂を保証。
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var spotify = new FakeSpotifyController();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), new ThrowingAudioPlayer(), searcher, spotify, new FakeClock());

        await Assert.ThrowsAsync<AudioException>(() => engine.RunAsync(FreeTalkCorner(), Djs));

        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);            // §3-1 完全静寂
        Assert.Equal(new SpotifyEvent.SetVolume(100), spotify.Events.Last()); // ダッキング相当の音量復元
    }

    // --- W13.5: コーナー頭の二層化（lead_in）+ その日の編成（CornerContext.CastDjIds） ---

    private static CornerEngine MakeEngine(SpyAudioPlayer audio, List<CornerEvent>? events = null, ITTSBackend? tts = null, IClock? clock = null)
    {
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        return new CornerEngine(
            llm, tts ?? new InMemoryTTS(), audio, searcher, new FakeSpotifyController(), clock ?? new FakeClock(),
            onEvent: events is null ? null : e => { lock (events) { events.Add(e); } },
            timeZone: TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task RunAsync_LeadIn_ExpandsTimeAtSpeechTime_SpokenByMainFirst()
    {
        var audio = new SpyAudioPlayer();
        var events = new List<CornerEvent>();
        // 15:07 UTC → {ampm}=午後 / {hour}=3 / {minute}=7。
        var engine = MakeEngine(audio, events, clock: new FakeClock(new DateTimeOffset(2026, 6, 12, 15, 7, 0, TimeSpan.Zero)));
        // cast 先頭＝metan（メイン。speaker 2）がリード文を読む。
        var context = new CornerContext(
            CastDjIds: new[] { "metan", "zundamon" },
            LeadIn: "{ampm}{hour}時{minute}分になりました。ここからはフリートークです。");

        var prepared = await engine.PrepareAsync(FreeTalkCorner(), Djs, context);
        await engine.RunAsync(prepared, Djs);

        var played = audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Equal(5, played.Count); // リード文 1 + 本編 4 行
        Assert.Equal("2:午後3時7分になりました。ここからはフリートークです。", played[0]); // メイン(metan=2) が発話直前展開で読む
        Assert.Contains(events, e => e is CornerEvent.LeadIn li && li.Text == "午後3時7分になりました。ここからはフリートークです。");
    }

    [Fact]
    public async Task RunAsync_NoLeadIn_SpeaksOnlyScript()
    {
        var audio = new SpyAudioPlayer();
        var events = new List<CornerEvent>();
        var engine = MakeEngine(audio, events);
        var context = new CornerContext(CastDjIds: new[] { "zundamon", "metan" }); // LeadIn なし（冒頭トーク相当）

        var prepared = await engine.PrepareAsync(FreeTalkCorner(), Djs, context);
        await engine.RunAsync(prepared, Djs);

        Assert.Equal(4, audio.Played.Count); // リード文なし、本編 4 行のみ
        Assert.DoesNotContain(events, e => e is CornerEvent.LeadIn);
    }

    [Fact]
    public async Task PrepareAsync_CastContext_OverridesCornerDjs_StoresResolvedCast_AndMainSpeaker()
    {
        var engine = MakeEngine(new SpyAudioPlayer());
        var context = new CornerContext(CastDjIds: new[] { "metan", "zundamon" }, LeadIn: "リード");

        var prepared = await engine.PrepareAsync(FreeTalkCorner(), Djs, context);

        Assert.Equal(new[] { "metan", "zundamon" }, prepared.CastDjIds); // corner.DjIds でなく当日 cast を保持
        Assert.Equal(2, prepared.LeadInSpeakerId);                       // メイン＝cast 先頭 metan(2)
    }

    [Fact]
    public async Task RunAsync_LeadInSynthFails_SkipsLeadIn_ContinuesScript()
    {
        // リード文の合成が一過性に失敗 → スキップして本編は流す（fail-tolerant）。本編は prepare で合成済み。
        var audio = new SpyAudioPlayer();
        var engine = MakeEngine(audio, tts: new FailOnTextTTS("FAILMARK"));
        var context = new CornerContext(CastDjIds: new[] { "zundamon", "metan" }, LeadIn: "FAILMARK");

        var prepared = await engine.PrepareAsync(FreeTalkCorner(), Djs, context);
        await engine.RunAsync(prepared, Djs); // throw しない

        Assert.Equal(4, audio.Played.Count); // リード文は流れず、本編 4 行は流れる
    }

    [Fact]
    public async Task RunAsync_LeadInCancellation_Propagates()
    {
        // キャンセル中はリード文失敗を握り潰さず伝播する（完全静寂 §3-1）。
        var engine = MakeEngine(new SpyAudioPlayer(), tts: new CtAwareTTS());
        var context = new CornerContext(CastDjIds: new[] { "zundamon", "metan" }, LeadIn: "リード");
        var prepared = await engine.PrepareAsync(FreeTalkCorner(), Djs, context); // 準備は非キャンセルで成功
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunAsync(prepared, Djs, cts.Token));
    }

    private sealed class ThrowingAudioPlayer : IAudioPlayer
    {
        public Task PlayAsync(byte[] wav, CancellationToken ct = default) => throw AudioException.PlaybackFailed();
    }

    /// <summary>指定の部分文字列を含むテキストの合成で例外を投げる（lead_in 一過性失敗の再現）。</summary>
    private sealed class FailOnTextTTS(string failMarker) : ITTSBackend
    {
        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
            => text.Contains(failMarker)
                ? throw new InvalidOperationException("lead-in synth failed")
                : Task.FromResult(Encoding.UTF8.GetBytes($"{speakerId}:{text}"));
    }

    /// <summary>ct を尊重する TTS（キャンセル時 OCE。lead_in のキャンセル伝播確認用）。</summary>
    private sealed class CtAwareTTS : ITTSBackend
    {
        public Task<byte[]> SynthesizeAsync(string text, int speakerId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Encoding.UTF8.GetBytes($"{speakerId}:{text}"));
        }
    }
}
