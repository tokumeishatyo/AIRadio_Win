using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W18 ステーション・ジャーナルの BroadcastEngine 配線: 正常終了でハイライト保存 / 当週の振り返りを冒頭コーナーの
/// 台本プロンプトにのみ注入 / 停止・キャンセル・無内容では保存しない。Mac <c>BroadcastEngineJournalTests</c> 移植。
/// Win は具象 <see cref="CornerEngine"/> ＋ 内容ルーティング LLM を注入する（Mac の FakeCornerRunner に相当する fake は無い）。
/// </summary>
public class BroadcastEngineJournalTests
{
    private const string OpTrack = "spotify:track:OP";
    private const string NewsTrack = "spotify:track:NEWS";
    private const string EdTrack = "spotify:track:ED";
    private const string FirstSongTrack = "spotify:track:FIRSTSONG";

    private const string ScriptResponse =
        "ずんだもん: こんにちは、なのだ。\n" +
        "四国めたん: どうも。\n" +
        "ずんだもん: 今日のテーマは音楽なのだ。\n" +
        "四国めたん: いいですわね。";

    private const string LetterResponse = "リスナー太郎\nきょうは良い一日でした。音楽を聴きながら過ごしました。";

    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    private static readonly DjProfile Sora = new("sora", "九州そら", 16, "おっとり穏やか");

    private static readonly IReadOnlyList<CornerTemplate> CornersWithGuest = new[]
    {
        new CornerTemplate("free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
        new CornerTemplate("letter", "お便り", "お便り", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
        new CornerTemplate("guest", "ゲストコーナー", "音楽", CornerFormat.Guest,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5,
            LeadIn: "本日のゲストは{guest}さんです。"),
    };

    // 全曜日メイン＝zundamon（FakeClock の日付に依存せず OP/ED 読み手を固定）。
    private static readonly WeeklyCast AllZundamonMain = new(
        Enum.GetValues<DayOfWeek>().ToDictionary(d => d, d => (IReadOnlyList<string>)new[] { "zundamon", "metan" }));

    private static ProgramBlueprint Blueprint(string? guestCornerId = null) => new(
        Title: "ケイラボAIラジオ",
        AnchorDjId: "zundamon",
        DefaultLength: ProgramLength.FromCorners(10),
        OpeningCritical: true,
        Song: new SongSegmentSpec(FirstSongTrack, "幕開けの曲", 100, 30),
        TalkCornerId: "free_talk",
        LetterCornerId: "letter",
        GuestCornerId: guestCornerId)
    {
        WeeklyCast = AllZundamonMain,
    };

    private static BroadcastThemes Themes() => new(
        Opening: new ThemedSegment(
            new ThemeConfig(null, OpTrack, IntroSeconds: 5, Volume: 100, DuckedVolume: 35, OutroSeconds: 10),
            new Dictionary<string, DjSpiel> { ["zundamon"] = new("オープニングです。{first_song}。", "OPタグ") }),
        News: new ThemeConfig("ニュースです", NewsTrack, 5, 100, 35, 10),
        Ending: new ThemedSegment(
            new ThemeConfig(null, EdTrack, 5, 100, 35, 10),
            new Dictionary<string, DjSpiel> { ["zundamon"] = new("エンディングです。") }),
        Greetings: new Greetings());

    private static SongPicker FirstSongPicker() => new(
        new ScriptedLLM("本日の曲 - 本日のアーティスト"),
        new FakeTrackSearcher(new[] { new TrackInfo(FirstSongTrack, "本日の曲", "本日のアーティスト", IsPlayable: true) }));

    private sealed record Harness(BroadcastEngine Engine, List<BroadcastEvent> Events);

    private static Harness BuildEngine(
        ISpotifyController spotify,
        IClock clock,
        IJournalStore journalStore,
        JournalSummarizer? journalSummarizer,
        ILLMBackend? talkLlm = null,
        Action<BroadcastEvent>? onEvent = null,
        TimeZoneInfo? timeZone = null)
    {
        var events = new List<BroadcastEvent>();
        var audio = new SpyAudioPlayer();
        var tts = new InMemoryTTS();

        talkLlm ??= new RecordingRoutingLLM("アイドル - YOASOBI", ScriptResponse, LetterResponse);
        var talkSearcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });

        var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock);
        var corner = new CornerEngine(talkLlm, tts, audio, talkSearcher, spotify, clock);
        var engine = new BroadcastEngine(
            themeSequencer, corner, FirstSongPicker(),
            _ => Task.FromResult("ニュース原稿"), spotify, clock,
            onEvent: e => { lock (events) { events.Add(e); } onEvent?.Invoke(e); },
            timeZone: timeZone ?? TimeZoneInfo.Utc,
            journalStore: journalStore,
            journalSummarizer: journalSummarizer);
        return new Harness(engine, events);
    }

    [Fact]
    public async Task SavesHighlight_OnNormalEnd_WithGuest()
    {
        var spotify = new FakeSpotifyController();
        var store = new InMemoryJournalStore();
        var summarizer = new JournalSummarizer(new ScriptedLLM("ゲストに九州そらさんを迎えました。"));
        var h = BuildEngine(spotify, new FakeClock(), store, summarizer);

        await h.Engine.RunAsync(
            new ProgramPlan(Blueprint(guestCornerId: "guest"), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs, guests: new[] { Sora });

        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal("ゲストに九州そらさんを迎えました。", store.Journal.Entries[^1].Highlight);
    }

    [Fact]
    public async Task InjectsCurrentWeekRecap_IntoOpeningCorner_NotLaterCorners()
    {
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock();
        var weekKey = StationJournal.WeekKeyFor(clock.Now, TimeZoneInfo.Utc);
        var store = new InMemoryJournalStore(new StationJournal(
            weekKey, new[] { new JournalEntry("1970-01-01", "米津玄師さんを特集しました。") }));
        var llm = new RecordingRoutingLLM("アイドル - YOASOBI", ScriptResponse, LetterResponse);
        // ゲストなしの素の N=2（冒頭 free_talk + 途中 free_talk + お便り）。summarizer ありだが無内容で保存はされない。
        var h = BuildEngine(spotify, clock, store, new JournalSummarizer(new ScriptedLLM("x")), talkLlm: llm);

        await h.Engine.RunAsync(
            new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs);

        // 冒頭コーナー（「番組の最初のコーナー」を含む台本リクエスト）にだけ振り返りが入る。
        var opening = llm.Prompts.Where(p => p.Contains("番組の最初のコーナー")).ToList();
        Assert.Single(opening);
        Assert.Contains("# 前回までの番組の振り返り", opening[0]);
        Assert.Contains("米津玄師", opening[0]);

        // 途中コーナー（「番組の途中のコーナー」を含む台本リクエスト）には振り返りを入れない。
        var later = llm.Prompts.Where(p => p.Contains("番組の途中のコーナー")).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, p => Assert.DoesNotContain("米津玄師", p));
        Assert.All(later, p => Assert.DoesNotContain("# 前回までの番組の振り返り", p));

        // 漏洩ガード（Win prompt レベルで Mac の type レベル保証に揃える）: ハイライトは**冒頭の台本リクエスト以外**の
        // どのリクエスト（各コーナーの選曲候補・お便り生成・冒頭曲選曲・ニュース）にも現れない。
        var withHighlight = llm.Prompts.Where(p => p.Contains("米津玄師")).ToList();
        Assert.All(withHighlight, p => Assert.Contains("番組の最初のコーナー", p));
        Assert.Equal(opening.Count, withHighlight.Count);
    }

    [Fact]
    public async Task DoesNotSave_OnCancel_EvenWithGuestContent()
    {
        var spotify = new FakeSpotifyController();
        var store = new InMemoryJournalStore();
        using var cts = new CancellationTokenSource();
        // 冒頭トーク（index 2）完了時にキャンセル → 次ループの ct チェックで OCE → save 経路に来ない。
        var h = BuildEngine(spotify, new FakeClock(), store, new JournalSummarizer(new ScriptedLLM("x")),
            onEvent: e => { if (e is BroadcastEvent.SegmentFinished { Index: 2, Kind: SegmentKind.Talk }) { cts.Cancel(); } });

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Engine.RunAsync(
            new ProgramPlan(Blueprint(guestCornerId: "guest"), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs, guests: new[] { Sora }, ct: cts.Token));

        Assert.Equal(0, store.SaveCount);
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished);
    }

    [Fact]
    public async Task DoesNotSave_OnNormalEnd_WhenNoGuestOrFeature()
    {
        var spotify = new FakeSpotifyController();
        var store = new InMemoryJournalStore();
        // ゲストも特集も無い N=2 → digest.HasContent==false → 保存しない（確定 A）。
        var h = BuildEngine(spotify, new FakeClock(), store, new JournalSummarizer(new ScriptedLLM("使われない")));

        await h.Engine.RunAsync(
            new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs);

        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Opening_ProceedsWithEmptyRecap_WhenLoadThrows()
    {
        // 壊れジャーナル（Load が throw）でも、JournalContextFromCurrentWeek が握り潰して空振り返り → 放送は完走。
        var spotify = new FakeSpotifyController();
        var store = new ThrowingJournalStore();
        var llm = new RecordingRoutingLLM("アイドル - YOASOBI", ScriptResponse, LetterResponse);
        var h = BuildEngine(spotify, new FakeClock(), store, new JournalSummarizer(new ScriptedLLM("x")), talkLlm: llm);

        await h.Engine.RunAsync(
            new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs);

        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]); // 例外を吐かず完走
        Assert.All(llm.Prompts, p => Assert.DoesNotContain("# 前回までの番組の振り返り", p)); // 振り返りは入らない
    }

    [Fact]
    public async Task SavesFromEmpty_AndFinishes_WhenLoadThrowsDuringSave()
    {
        // 保存時の Load も throw するが、SaveJournalAsync が握り潰して Empty から積み直し → 放送完走 + 1 エントリ保存。
        var spotify = new FakeSpotifyController();
        var store = new ThrowingJournalStore();
        var summarizer = new JournalSummarizer(new ScriptedLLM("ゲストに九州そらさんを迎えました。"));
        var h = BuildEngine(spotify, new FakeClock(), store, summarizer);

        await h.Engine.RunAsync(
            new ProgramPlan(Blueprint(guestCornerId: "guest"), ProgramLength.FromCorners(2)),
            Themes(), CornersWithGuest, Djs, guests: new[] { Sora });

        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.Saved);
        Assert.Single(store.Saved!.Entries); // Empty から積み直したので 1 件
        Assert.Equal("ゲストに九州そらさんを迎えました。", store.Saved!.Entries[^1].Highlight);
    }

    /// <summary>プロンプト内容で応答を振り分け、かつ全プロンプトを記録する決定論 LLM（注入検証用）。</summary>
    private sealed class RecordingRoutingLLM(string candidates, string script, string letter) : ILLMBackend
    {
        private readonly object _lock = new();
        private readonly List<string> _prompts = new();

        public IReadOnlyList<string> Prompts
        {
            get { lock (_lock) { return _prompts.ToList(); } }
        }

        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { _prompts.Add(request.Prompt); }
            var p = request.Prompt;
            if (p.Contains("候補")) { return Task.FromResult(candidates); }
            if (p.Contains("会話台本")) { return Task.FromResult(script); }
            return Task.FromResult(letter); // 架空お便りの生成。
        }
    }
}
