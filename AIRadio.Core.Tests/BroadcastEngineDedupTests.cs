using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W-DEDUP の dedup 機構を BroadcastEngine + CornerEngine 経由で検証する（共有 <see cref="PlayedSongHistory"/>・URI 衝突 fake）。
/// 既定ハーネスは冒頭曲と締め曲が別 URI ゆえ素の green は dedup を証明しない（OBS-2）。意図的に URI を衝突させ、
/// 共有履歴が「2 度目を弾く」動線を作る:
/// - 同一放送内: 冒頭曲が X を予約 → 締め曲は X が弾かれ次候補 Y を採る。
/// - 放送跨ぎ: 同一履歴で 2 回放送すると、冒頭曲 URI が A→B に変わる（A が既出で弾かれる）。
/// </summary>
public class BroadcastEngineDedupTests
{
    private const string OpTrack = "spotify:track:OP";
    private const string EdTrack = "spotify:track:ED";
    private const string NewsTrack = "spotify:track:NEWS";

    private const string ScriptResponse =
        "ずんだもん: こんにちは、なのだ。\n" +
        "四国めたん: どうも。\n" +
        "ずんだもん: 今日のテーマは音楽なのだ。\n" +
        "四国めたん: いいですわね。";

    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    // 既存テストの安定化と同様、全曜日メイン＝zundamon（FakeClock の日付に依存せず OP/ED 読み手を固定）。
    private static readonly WeeklyCast AllZundamonMain = new(
        Enum.GetValues<DayOfWeek>().ToDictionary(d => d, d => (IReadOnlyList<string>)new[] { "zundamon", "metan" }));

    private static readonly IReadOnlyList<CornerTemplate> Corners = new[]
    {
        new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:cornerfallback", Volume: 100, PlaySeconds: 5),
        new CornerTemplate(
            "letter", "お便り", "お便り", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:cornerfallback", Volume: 100, PlaySeconds: 5),
    };

    private static ProgramPlan Plan() => new(
        new ProgramBlueprint(
            "ケイラボAIラジオ", "zundamon", ProgramLength.FromCorners(1), true,
            new SongSegmentSpec("spotify:track:SONGFALLBACK", "幕開け", 100, 30),
            "free_talk", "letter", NewsDjId: null)
        {
            WeeklyCast = AllZundamonMain,
        },
        ProgramLength.FromCorners(1));

    private static BroadcastThemes Themes() => new(
        Opening: new ThemedSegment(
            new ThemeConfig(null, OpTrack, 5, 100, 35, 10),
            new Dictionary<string, DjSpiel> { ["zundamon"] = new("オープニングです。{first_song}。", "OPタグ") }),
        News: new ThemeConfig("ニュースです", NewsTrack, 5, 100, 35, 10),
        Ending: new ThemedSegment(
            new ThemeConfig(null, EdTrack, 5, 100, 35, 10),
            new Dictionary<string, DjSpiel> { ["zundamon"] = new("エンディングです。") }),
        Greetings: new Greetings());

    private static (BroadcastEngine Engine, FakeSpotifyController Spotify) BuildEngine(
        PlayedSongHistory history, SongPicker firstSongPicker, ILLMBackend cornerLlm, ITrackSearcher cornerSearcher)
    {
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock();
        var audio = new SpyAudioPlayer();
        var tts = new InMemoryTTS();
        var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock);
        var corner = new CornerEngine(cornerLlm, tts, audio, cornerSearcher, spotify, clock, songHistory: history);
        var engine = new BroadcastEngine(
            themeSequencer, corner, firstSongPicker,
            _ => Task.FromResult("ニュース原稿"), spotify, clock, songHistory: history);
        return (engine, spotify);
    }

    private static List<string> Plays(FakeSpotifyController spotify)
        => spotify.Events.OfType<SpotifyEvent.Play>().Select(p => p.Uri).ToList();

    [Fact]
    public async Task SameBroadcast_FirstSongReserves_CornerSongAvoidsIt()
    {
        var history = new PlayedSongHistory(100);
        var x = new TrackInfo("spotify:track:X", "曲X", "アーティストX", IsPlayable: true);
        var y = new TrackInfo("spotify:track:Y", "曲Y", "アーティストY", IsPlayable: true);

        // 冒頭曲: 候補 song-x → X を予約（先頭再生可能曲）。
        var firstPicker = new SongPicker(new FixedLLM("song-x - ax"), new MappedSearcher(("song-x", x)));
        // 締め曲: 候補 song-x（既出 X→弾かれる）→ song-y（未既出 Y→採用）。冒頭曲と同一履歴を共有。
        var cornerLlm = new RoutingLLM("song-x - ax\nsong-y - ay", ScriptResponse);
        var cornerSearcher = new MappedSearcher(("song-x", x), ("song-y", y));

        var (engine, spotify) = BuildEngine(history, firstPicker, cornerLlm, cornerSearcher);

        await engine.RunAsync(Plan(), Themes(), Corners, Djs);

        // OP → 冒頭曲 X → 締め曲 Y（X ではない）→ ED。共有履歴で冒頭曲 X が締め曲から弾かれた。
        // dedup が無ければ締め曲も X を採り [OP, X, X, ED] になる（Y が採られたことが dedup 発火の証）。
        Assert.Equal(new[] { OpTrack, "spotify:track:X", "spotify:track:Y", EdTrack }, Plays(spotify));
    }

    [Fact]
    public async Task AcrossBroadcasts_SharedHistory_FirstSongChangesFromAToB()
    {
        var history = new PlayedSongHistory(100);
        var a = new TrackInfo("spotify:track:A", "曲A", "アーティストA", IsPlayable: true);
        var b = new TrackInfo("spotify:track:B", "曲B", "アーティストB", IsPlayable: true);
        var z = new TrackInfo("spotify:track:Z", "曲Z", "アーティストZ", IsPlayable: true);

        // 冒頭曲: 候補 song-a → song-b の順。1 回目は A 採用、2 回目は A 既出で弾かれ B。
        SongPicker FirstPicker() => new(
            new FixedLLM("song-a - aa\nsong-b - bb"),
            new MappedSearcher(("song-a", a), ("song-b", b)));
        // 締め曲は別 URI Z（冒頭曲の判別に干渉しない。2 回目は Z 既出→fallback でも無害）。
        RoutingLLM CornerLlm() => new("song-z - cz", ScriptResponse);
        MappedSearcher CornerSearcher() => new(("song-z", z));

        // 同一履歴を共有しつつ engine を作り直す（composition が放送ごとに engine を再生成し履歴のみ保持するのを再現）。
        var (engine1, spotify1) = BuildEngine(history, FirstPicker(), CornerLlm(), CornerSearcher());
        await engine1.RunAsync(Plan(), Themes(), Corners, Djs);
        Assert.Equal("spotify:track:A", Plays(spotify1)[1]);   // 冒頭曲 = A（Plays[0]=OP）

        var (engine2, spotify2) = BuildEngine(history, FirstPicker(), CornerLlm(), CornerSearcher());
        await engine2.RunAsync(Plan(), Themes(), Corners, Djs);
        Assert.Equal("spotify:track:B", Plays(spotify2)[1]);   // A は既出で弾かれ B に変わる（放送跨ぎ保持）
    }

    // --- fake 群（既存 interface の実装。新 interface を作らない＝§3-5） ---

    /// <summary>常に同じ候補文字列を返す LLM（冒頭曲ピッカー用＝候補のみ生成）。</summary>
    private sealed class FixedLLM(string response) : ILLMBackend
    {
        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
            => Task.FromResult(response);
    }

    /// <summary>「候補」プロンプトには候補列、それ以外（会話台本）には台本を返す決定論 LLM（締め曲コーナー用）。</summary>
    private sealed class RoutingLLM(string candidates, string script) : ILLMBackend
    {
        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
            => Task.FromResult(request.Prompt.Contains("候補") ? candidates : script);
    }

    /// <summary>クエリに含まれる title をキーに対応する 1 曲を返す検索 fake（候補ごとに別 URI を返し dedup 動線を作る）。</summary>
    private sealed class MappedSearcher : ITrackSearcher
    {
        private readonly IReadOnlyList<(string Needle, TrackInfo Track)> _map;

        public MappedSearcher(params (string Needle, TrackInfo Track)[] map) => _map = map;

        public Task<IReadOnlyList<TrackInfo>> SearchAsync(string query, int limit, CancellationToken ct = default)
        {
            foreach (var (needle, track) in _map)
            {
                if (query.Contains(needle, StringComparison.Ordinal))
                {
                    return Task.FromResult<IReadOnlyList<TrackInfo>>(new[] { track });
                }
            }
            return Task.FromResult<IReadOnlyList<TrackInfo>>(Array.Empty<TrackInfo>());
        }

        public Task<bool> IsPlayableAsync(string uri, CancellationToken ct = default) => Task.FromResult(true);
    }
}
