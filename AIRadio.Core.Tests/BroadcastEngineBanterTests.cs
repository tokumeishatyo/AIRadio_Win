using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W-DLG 掛け合い指示の BroadcastEngine 配線: reimu+marisa の free_talk トーク台本にのみ注入し、
/// letter / 非トリガー日 / 未配線では注入しない。RecordingLLM で台本リクエストの内容を検査する（W18 journal と同型）。
/// </summary>
public class BroadcastEngineBanterTests
{
    private const string Directive = "霊夢が問いかけ、魔理沙が答える掛け合い。（テスト）";

    private const string ScriptResponse =
        "博麗霊夢: ねえ魔理沙。\n博麗霊夢: 今日のテーマは音楽よ。\n霧雨魔理沙: いいぜ。\n博麗霊夢: 始めましょう。";
    private const string LetterResponse = "リスナー太郎\n良い一日でした。音楽を聴いて過ごしました。";

    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("reimu", "博麗霊夢", 1001, "ツッコミ"),
        new DjProfile("marisa", "霧雨魔理沙", 1002, "元気"),
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品"),
    };

    private static WeeklyCast Cast(params string[] ids) => new(
        Enum.GetValues<DayOfWeek>().ToDictionary(d => d, d => (IReadOnlyList<string>)ids));

    private static readonly IReadOnlyList<CornerTemplate> Corners = new[]
    {
        new CornerTemplate("free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "reimu", "marisa" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
        new CornerTemplate("letter", "お便り", "お便り", CornerFormat.Letter,
            new[] { "reimu", "marisa" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
    };

    private static ProgramBlueprint Blueprint(WeeklyCast cast, string anchor) => new(
        Title: "ケイラボAIラジオ",
        AnchorDjId: anchor,
        DefaultLength: ProgramLength.FromCorners(10),
        OpeningCritical: true,
        Song: new SongSegmentSpec("spotify:track:fb", "幕開け", 100, 30),
        TalkCornerId: "free_talk",
        LetterCornerId: "letter",
        NewsDjId: null)
    {
        WeeklyCast = cast,
    };

    private static BroadcastThemes Themes(string mainId) => new(
        Opening: new ThemedSegment(
            new ThemeConfig(null, "spotify:track:OP", 5, 100, 35, 10),
            new Dictionary<string, DjSpiel> { [mainId] = new("オープニングです。{first_song}。", "OPタグ") }),
        News: new ThemeConfig("ニュースです", "spotify:track:NEWS", 5, 100, 35, 10),
        Ending: new ThemedSegment(
            new ThemeConfig(null, "spotify:track:ED", 5, 100, 35, 10),
            new Dictionary<string, DjSpiel> { [mainId] = new("エンディングです。") }),
        Greetings: new Greetings());

    private static SongPicker FirstSongPicker() => new(
        new ScriptedLLM("本日の曲 - 本日のアーティスト"),
        new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:FS", "本日の曲", "本日のアーティスト", IsPlayable: true) }));

    private static BanterDirectives ReimuMarisaRule() =>
        new(new[] { new BanterRule("reimu", new[] { "marisa" }, Directive) });

    private static (BroadcastEngine Engine, RecordingLLM Llm) Build(BanterDirectives? banter)
    {
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock();
        var llm = new RecordingLLM(ScriptResponse, LetterResponse);
        var searcher = new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        var sequencer = new ThemeSequencer(llm.Tts, llm.Audio, spotify, clock);
        var corner = new CornerEngine(llm, llm.Tts, llm.Audio, searcher, spotify, clock);
        var engine = new BroadcastEngine(
            sequencer, corner, FirstSongPicker(),
            _ => Task.FromResult("ニュース原稿"), spotify, clock,
            timeZone: TimeZoneInfo.Utc,
            banterDirectives: banter);
        return (engine, llm);
    }

    private static Task Run(BroadcastEngine engine, WeeklyCast cast, string mainId) => engine.RunAsync(
        new ProgramPlan(Blueprint(cast, mainId), ProgramLength.FromCorners(2)), Themes(mainId), Corners, Djs);

    [Fact]
    public async Task ReimuMarisa_FreeTalk_InjectsDirective_NotLetter()
    {
        var (engine, llm) = Build(ReimuMarisaRule());

        await Run(engine, Cast("reimu", "marisa"), "reimu");

        var withDir = llm.Prompts.Where(p => p.Contains(Directive)).ToList();
        Assert.NotEmpty(withDir);                                    // free_talk に注入された
        Assert.All(withDir, p => Assert.Contains("フリートーク", p)); // 注入は free_talk の台本のみ
        // letter コーナーの台本リクエスト（お便り本文セクションを含む）には注入されない。
        Assert.All(llm.Prompts.Where(p => p.Contains("# リスナーからのお便り")),
            p => Assert.DoesNotContain(Directive, p));
    }

    [Fact]
    public async Task NonTriggerDay_NoInjection()
    {
        var (engine, llm) = Build(ReimuMarisaRule());

        await Run(engine, Cast("zundamon", "metan"), "zundamon"); // main=zundamon → ルール不一致

        Assert.All(llm.Prompts, p => Assert.DoesNotContain(Directive, p));
    }

    [Fact]
    public async Task Unwired_NoInjection()
    {
        var (engine, llm) = Build(banter: null); // banter 未配線（従来挙動）

        await Run(engine, Cast("reimu", "marisa"), "reimu");

        Assert.All(llm.Prompts, p => Assert.DoesNotContain(Directive, p));
    }

    [Fact]
    public async Task GuestCorner_DoesNotInjectDirective()
    {
        var (engine, llm) = Build(ReimuMarisaRule());
        var corners = new[]
        {
            Corners[0], // free_talk
            Corners[1], // letter
            new CornerTemplate("guest", "ゲストコーナー", "音楽", CornerFormat.Guest,
                new[] { "reimu", "marisa" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5,
                LeadIn: "本日のゲストは{guest}さんです。"),
        };
        var blueprint = Blueprint(Cast("reimu", "marisa"), "reimu") with { GuestCornerId = "guest" };

        await engine.RunAsync(new ProgramPlan(blueprint, ProgramLength.FromCorners(2)),
            Themes("reimu"), corners, Djs, guests: new[] { new DjProfile("sora", "九州そら", 16, "おっとり") });

        Assert.Contains(llm.Prompts, p => p.Contains(Directive) && p.Contains("フリートーク"));      // free_talk には注入
        Assert.All(llm.Prompts.Where(p => p.Contains("詳しい専門家")), p => Assert.DoesNotContain(Directive, p)); // ゲストには非注入
    }

    /// <summary>台本リクエストを記録し、内容で応答を振り分ける決定論 LLM（注入検証用）。TTS/Audio も保持する。</summary>
    private sealed class RecordingLLM : ILLMBackend
    {
        private readonly object _lock = new();
        private readonly List<string> _prompts = new();
        private readonly string _script;
        private readonly string _letter;

        public InMemoryTTS Tts { get; } = new();
        public SpyAudioPlayer Audio { get; } = new();

        public RecordingLLM(string script, string letter) { _script = script; _letter = letter; }

        public IReadOnlyList<string> Prompts
        {
            get { lock (_lock) { return _prompts.ToList(); } }
        }

        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { _prompts.Add(request.Prompt); }
            var p = request.Prompt;
            if (p.Contains("候補")) { return Task.FromResult("アイドル - YOASOBI"); }
            if (p.Contains("会話台本")) { return Task.FromResult(_script); }
            return Task.FromResult(_letter); // 架空お便りの生成。
        }
    }
}
