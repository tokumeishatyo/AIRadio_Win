using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W7→W13 BroadcastEngine の進行契約（OP → 冒頭曲 → 本編 → ED、ローリング先読み準備、「ED で終了」）。
/// W13 で <c>ProgramPlan</c>（コーナー数 N 駆動）に移行。本編は <c>talk, talk, letter, news</c> の繰り返しのため、
/// テストは N=1（OP→song→talk→ED）/ N=2（OP→song→t1→t2→letter→news→ED）等で組む。
/// 具象 <see cref="ThemeSequencer"/> / <see cref="CornerEngine"/> / <see cref="SongPicker"/> へ既存 interface の
/// fake を注入する（新 interface を作らない, §3-5）。準備系 fake はすべて同期完了（<c>Task.FromResult</c>）のため、
/// <c>EnsurePrepared</c> 時点でコーナーは準備完了済みになり ED 判定は決定論的（未準備は専用のブロッキング fake で再現）。
/// </summary>
public class BroadcastEngineTests
{
    private const string OpTrack = "spotify:track:OP";
    private const string NewsTrack = "spotify:track:NEWS";
    private const string EdTrack = "spotify:track:ED";
    private const string FirstSongTrack = "spotify:track:FIRSTSONG";
    private const string TalkSongTrack = "spotify:track:idol";
    private const string SongFallback = "spotify:track:SONGFALLBACK";

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

    private static readonly IReadOnlyList<DjProfile> DjsWithNewsDj = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
        new DjProfile("ryusei", "青山龍星", 13, "ニュースキャスター"),
    };

    private static readonly IReadOnlyList<CornerTemplate> Corners = new[]
    {
        new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
        new CornerTemplate(
            "letter", "お便り", "お便り", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
    };

    private static ProgramBlueprint MakeBlueprint(int songPlaySeconds = 30, string? newsDjId = null) => new(
        Title: "ケイラボAIラジオ",
        AnchorDjId: "zundamon",
        DefaultLength: ProgramLength.FromCorners(10),
        OpeningCritical: true,
        Song: new SongSegmentSpec(SongFallback, "幕開けの曲", 100, songPlaySeconds),
        TalkCornerId: "free_talk",
        LetterCornerId: "letter",
        NewsDjId: newsDjId);

    private static ProgramPlan MakePlan(ProgramLength length, int songPlaySeconds = 30, string? newsDjId = null)
        => new(MakeBlueprint(songPlaySeconds, newsDjId), length);

    private static BroadcastThemes MakeThemes(
        string openingAnnouncement = "オープニングです。{first_song}。",
        Greetings? greetings = null) => new(
        Opening: new ThemeConfig("OPタグ", OpTrack, IntroSeconds: 5, Volume: 100, DuckedVolume: 35, OutroSeconds: 10),
        OpeningAnnouncement: openingAnnouncement,
        News: new ThemeConfig("ニュースです", NewsTrack, 5, 100, 35, 10),
        Ending: new ThemeConfig(null, EdTrack, 5, 100, 35, 10),
        EndingAnnouncement: "エンディングです。",
        Greetings: greetings ?? new Greetings());

    private sealed record Harness(
        BroadcastEngine Engine, List<BroadcastEvent> Events, SpyAudioPlayer Audio, Func<int> NewsCalls);

    // 冒頭曲の選曲（エンジン）とトーク（CornerEngine 内部の選曲）を別 fake にして URI を区別する。
    private static SongPicker DefaultFirstSongPicker() => new(
        new ScriptedLLM("本日の曲 - 本日のアーティスト"),
        new FakeTrackSearcher(new[] { new TrackInfo(FirstSongTrack, "本日の曲", "本日のアーティスト", IsPlayable: true) }));

    private static Harness BuildEngine(
        ISpotifyController spotify,
        IClock clock,
        SongPicker? firstSongPicker = null,
        ILLMBackend? talkLlm = null,
        Func<CancellationToken, Task<string>>? news = null,
        TimeZoneInfo? timeZone = null,
        IReadOnlyList<DjProfile>? djs = null,
        Action<BroadcastEvent>? onEvent = null)
    {
        var events = new List<BroadcastEvent>();
        var audio = new SpyAudioPlayer();
        var tts = new InMemoryTTS();

        firstSongPicker ??= DefaultFirstSongPicker();
        // 内容ルーティングの talk LLM（候補→曲名、会話台本→台本、架空お便り→お便り）。複数コーナーの並行準備でも決定論的。
        talkLlm ??= new RoutingLLM(candidates: "アイドル - YOASOBI", script: ScriptResponse, letter: LetterResponse);
        var talkSearcher = new FakeTrackSearcher(new[] { new TrackInfo(TalkSongTrack, "アイドル", "YOASOBI", IsPlayable: true) });

        var newsCalls = new int[1];
        var baseNews = news ?? (_ => Task.FromResult("ニュース原稿"));
        Func<CancellationToken, Task<string>> countedNews = token =>
        {
            lock (newsCalls) { newsCalls[0]++; }
            return baseNews(token);
        };

        var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock);
        var corner = new CornerEngine(talkLlm, tts, audio, talkSearcher, spotify, clock);
        var engine = new BroadcastEngine(
            themeSequencer, corner, firstSongPicker, countedNews, spotify, clock,
            e => { lock (events) { events.Add(e); } onEvent?.Invoke(e); },
            timeZone);

        return new Harness(engine, events, audio, () => { lock (newsCalls) { return newsCalls[0]; } });
    }

    private static List<string> Plays(FakeSpotifyController spotify)
        => spotify.Events.OfType<SpotifyEvent.Play>().Select(p => p.Uri).ToList();

    [Fact]
    public async Task HappyPath_RunsSegmentsInOrder_WithFirstSong_ThenFinishesAndPauses()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        // N=2: OP → song → t1(free_talk) → t2(free_talk) → letter → news → ED。
        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(2)), MakeThemes(), Corners, Djs);

        // Play 順: OP テーマ → 冒頭曲 → t1/t2/letter の締め曲 → news テーマ → ED テーマ。
        Assert.Equal(
            new[] { OpTrack, FirstSongTrack, TalkSongTrack, TalkSongTrack, TalkSongTrack, NewsTrack, EdTrack },
            Plays(spotify));

        Assert.Equal(1, h.NewsCalls());
        Assert.Equal(new BroadcastEvent.SegmentStarted(0, SegmentKind.Opening), h.Events[0]);
        Assert.Contains(h.Events, e => e is BroadcastEvent.SongStarted s && s.Index == 1 && s.Track.Uri == FirstSongTrack);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new BroadcastEvent.SegmentFinished(6, SegmentKind.Ending), h.Events);
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentFailed);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // 完全静寂（§3-1）。
    }

    [Fact]
    public async Task Opening_ExpandsFirstSong_ReadByAnchor()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs);

        // OP の {first_song} が冒頭曲の「<artist>で、「<title>」」に展開され、anchor(ずんだもん=3) が読む。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.StartsWith("3:") && s.Contains("本日のアーティストで、「本日の曲」"));
    }

    [Fact]
    public async Task Opening_ExpandsTimePlaceholders_AtSpeechTime()
    {
        // 発話直前展開（w8 §3）: 固定時刻 2026-06-12 15:07 UTC を timeZone=UTC で解釈 →
        // {greeting}=こんにちは（昼）/ {month}=6 / {day}=12 / {ampm}=午後 / {hour}=3。
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 12, 15, 7, 0, TimeSpan.Zero));
        var h = BuildEngine(spotify, clock, timeZone: TimeZoneInfo.Utc);
        var themes = MakeThemes(openingAnnouncement: "{greeting}。{month}月{day}日、{ampm}{hour}時。{first_song}。");

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), themes, Corners, Djs);

        // anchor(ずんだもん=3) が、実時刻 + 冒頭曲の曲振りで読む（ゼロ埋めなし）。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s =>
            s.StartsWith("3:") && s.Contains("こんにちは。6月12日、午後3時。本日のアーティストで、「本日の曲」。"));
    }

    [Fact]
    public async Task News_ExpandsTimePlaceholders_TwoStage_ReadByAnchor()
    {
        // 二段展開: Provider 原稿に残った {hour12}/{minute} を、エンジンが発話直前に実時刻で展開する。
        // 0:05 UTC → {hour12}=0 / {minute}=5（午前午後なし・ゼロ埋めなし）。N=2 で news を含む。
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 9, 0, 5, 0, TimeSpan.Zero));
        var h = BuildEngine(
            spotify, clock, timeZone: TimeZoneInfo.Utc,
            news: _ => Task.FromResult("時刻は{hour12}時{minute}分になりました。ニュース速報。"));

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(2)), MakeThemes(), Corners, Djs);

        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.Contains("時刻は0時5分になりました。ニュース速報。"));
    }

    [Fact]
    public async Task Ending_NoTimePlaceholders_PassesThroughVerbatim()
    {
        // ED は時刻プレースホルダ非含有 → 同じ時刻辞書をマージしても無置換（w8 §2 ED は out。リグレッション固定）。
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 12, 15, 7, 0, TimeSpan.Zero));
        var h = BuildEngine(spotify, clock, timeZone: TimeZoneInfo.Utc);

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs);

        // anchor(ずんだもん=3) が ED 原文をそのまま読み、時刻文字列（午前/午後）が一切混入しない。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.StartsWith("3:") && s.Contains("エンディングです"));
        Assert.DoesNotContain(spoken, s => s.Contains("午前") || s.Contains("午後"));
    }

    [Fact]
    public async Task RollingPreparation_TalkPreparedBeforeFirstSongPlays()
    {
        // 無音解消の核: トーク準備（LLM 台本）が冒頭曲の再生開始より前に始まっている。
        var trace = new List<string>();
        var spotify = new TracingSpotify(trace);
        var talkLlm = new TracingLLM(new ScriptedLLM("アイドル - YOASOBI", ScriptResponse), trace);
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: talkLlm);

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs);

        var firstTalkPrep = trace.IndexOf("talk-llm");
        var firstSongPlay = trace.IndexOf($"play:{FirstSongTrack}");
        Assert.True(firstTalkPrep >= 0, "トーク準備の LLM 呼び出しが記録されていない");
        Assert.True(firstSongPlay >= 0, "冒頭曲の再生が記録されていない");
        Assert.True(firstTalkPrep < firstSongPlay, "トーク準備は冒頭曲の再生より前に始まる（ローリング先読み）");
    }

    [Fact]
    public async Task NewsSegment_ReadByDedicatedNewsDj_WhenDjIdSet()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock(), djs: DjsWithNewsDj, news: _ => Task.FromResult("ニュース速報テスト"));

        await h.Engine.RunAsync(
            MakePlan(ProgramLength.FromCorners(2), newsDjId: "ryusei"), MakeThemes(), Corners, DjsWithNewsDj);

        // ニュースは専任の ryusei(speaker 13) が読む（anchor=zundamon(3) ではない）。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.StartsWith("13:") && s.Contains("ニュース速報テスト"));
        Assert.DoesNotContain(spoken, s => s.StartsWith("3:") && s.Contains("ニュース速報テスト"));
    }

    [Fact]
    public async Task Preflight_UnknownNewsDj_FailsFast()
    {
        // news.dj_id は plan に news が無くても（N=1）blueprint フィールドとして無条件検証（Mac 一致）。
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1), newsDjId: "ghost"), MakeThemes(), Corners, Djs));

        Assert.Empty(spotify.Events);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task FullPlaySong_WaitsForFinish_EmitsSongFinished()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1), songPlaySeconds: 0), MakeThemes(), Corners, Djs);

        Assert.Contains(h.Events, e => e is BroadcastEvent.SongFinished s && s.Index == 1);
    }

    [Fact]
    public async Task TalkSegmentFails_FailTolerant_SkipsAndContinues()
    {
        var spotify = new FakeSpotifyController();
        // 締め曲の選曲は成功、台本生成で応答が尽きて EmptyResponse → トークだけ失敗。N=1（OP→song→talk→ED）。
        var talkLlm = new ScriptedLLM("アイドル - YOASOBI");
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: talkLlm);

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs); // 非 critical なので throw しない。

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 2 && f.Kind == SegmentKind.Talk && f.Code == "E-LLM-EMPTY-RESPONSE-001");
        // 冒頭曲・ED は実行され、最後まで完走する（トーク失敗をスキップして継続）。
        Assert.Contains(new BroadcastEvent.SegmentFinished(3, SegmentKind.Ending), h.Events);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);
        var plays = Plays(spotify);
        Assert.Contains(FirstSongTrack, plays);            // 冒頭曲は流れている
        Assert.DoesNotContain(TalkSongTrack, plays);       // 失敗トークは締め曲を流していない
    }

    [Fact]
    public async Task CriticalOpeningFails_AbortsBroadcast_AndPauses()
    {
        var spotify = new ThrowOnPlaySpotify(OpTrack, SpotifyException.AuthFailed("token expired"));
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<BroadcastException>(
            () => h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs));

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 0 && f.Kind == SegmentKind.Opening && f.Code == "E-SPT-AUTH-FAILED-001");
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished);     // 中止
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentStarted s && s.Kind == SegmentKind.Song);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);                       // §3-1
    }

    [Fact]
    public async Task PreCancelled_ThrowsImmediately_NoSegmentsPlayed()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs, ct: cts.Token));

        Assert.Empty(h.Events);                                                  // ループに入らず SegmentStarted も出ない
        Assert.DoesNotContain(spotify.Events, e => e is SpotifyEvent.Play);      // 何も再生しない
    }

    [Fact]
    public async Task CancellationWrappedAsDomainError_RethrownAsCancellation_NotSegmentFailed()
    {
        using var cts = new CancellationTokenSource();
        // OP 再生時に ct を取り消してからドメインエラーを投げる（取消された HttpClient のラップを再現）。
        var spotify = new CancelThenThrowSpotify(cts, OpTrack);
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs, ct: cts.Token));

        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentFailed); // 誤判定しない
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);
    }

    [Fact]
    public async Task Preflight_UnknownAnchorDj_FailsFast_NoAudio()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());
        var plan = new ProgramPlan(MakeBlueprint() with { AnchorDjId = "ghost" }, ProgramLength.FromCorners(1));

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(plan, MakeThemes(), Corners, Djs));

        Assert.Empty(spotify.Events); // 音を出す前に fail-fast
        Assert.Empty(h.Events);
        Assert.Equal(0, h.NewsCalls());
    }

    [Fact]
    public async Task Preflight_UnknownTalkCorner_FailsFast_NoAudio()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Array.Empty<CornerTemplate>(), Djs));

        Assert.Empty(spotify.Events);
        Assert.Empty(h.Events);
    }

    // --- ED で終了（w13 §3-4） ---

    [Fact]
    public async Task Ending_PlaysPreparedNextFreeTalk_ThenEnds()
    {
        // ①直後のトーク（free_talk）が準備完了済みなら、それを流してから ED（お便り・ニュースは飛ばす）。
        // t1（index 2）完了時に ED 要求 → t2（index 3、同期準備済み）まで流して ED。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        Harness? h = null;
        h = BuildEngine(spotify, new FakeClock(), onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentFinished { Index: 2, Kind: SegmentKind.Talk })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(10)), MakeThemes(), Corners, Djs, control);

        // t1 + 準備済みの t2 を流し、letter / news を飛ばして ED。
        Assert.Equal(
            new[] { OpTrack, FirstSongTrack, TalkSongTrack, TalkSongTrack, EdTrack }, Plays(spotify));
        Assert.DoesNotContain(NewsTrack, Plays(spotify));   // news は流れない
        Assert.Equal(0, h.NewsCalls());                     // 窓の外（news 準備）に到達しない
        Assert.Contains(h.Events, e => e is BroadcastEvent.EndingRequested);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    [Fact]
    public async Task Ending_SkipsLetterAndGoesStraightToEnding()
    {
        // ②直後がお便り（トーク以外）なら準備済みでも飛ばして即 ED。
        // t2（index 3）完了時に ED 要求 → 直後はお便り（index 4）→ 即 ED。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var h = BuildEngine(spotify, new FakeClock(), onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentFinished { Index: 3, Kind: SegmentKind.Talk })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(10)), MakeThemes(), Corners, Djs, control);

        // t1, t2 のみ（letter は実行しない＝締め曲は 2 回）。
        Assert.Equal(
            new[] { OpTrack, FirstSongTrack, TalkSongTrack, TalkSongTrack, EdTrack }, Plays(spotify));
        Assert.Contains(h.Events, e => e is BroadcastEvent.EndingRequested);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    [Fact]
    public async Task Ending_UnpreparedNextTalk_GoesStraightToEnding_AndCancelsRest()
    {
        // ③直後のトークが未準備なら待たずに即 ED + 残りの準備はキャンセル。
        // song（index 1）開始時に ED 要求 → トーク（index 2）の準備は候補生成でブロック中＝未準備。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var blockingLlm = new BlockingCandidatesLLM();
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: blockingLlm, onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentStarted { Index: 1, Kind: SegmentKind.Song })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(10)), MakeThemes(), Corners, Djs, control);

        // 未準備のトークは待たず、OP → 冒頭曲 → ED（トーク締め曲は流れない）。
        Assert.Equal(new[] { OpTrack, FirstSongTrack, EdTrack }, Plays(spotify));
        Assert.DoesNotContain(TalkSongTrack, Plays(spotify));
        Assert.Contains(h.Events, e => e is BroadcastEvent.EndingRequested);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        // 残りの準備はキャンセルされる（RunAsync の finally で Drain 完了まで待つため、戻り時点で観測済み）。
        Assert.True(blockingLlm.SawCancellation, "ブロック中のコーナー準備がキャンセルされていない");
    }

    [Fact]
    public async Task Ending_EndlessProgram_EndsOnRequest()
    {
        // エンドレス番組も ED 要求で終了する（plan に ED がないため合成）。Core 専用（UI 非露出）の保証。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var h = BuildEngine(spotify, new FakeClock(), onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentFinished { Index: 2, Kind: SegmentKind.Talk })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.Endless), MakeThemes(), Corners, Djs, control);

        // t1 完了で ED → 準備済み t2 を流して合成 ED。エンドレスでも停止する。
        Assert.Equal(
            new[] { OpTrack, FirstSongTrack, TalkSongTrack, TalkSongTrack, EdTrack }, Plays(spotify));
        Assert.Contains(h.Events, e => e is BroadcastEvent.EndingRequested);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    // --- ローリング準備の窓上限 / news 出現毎生成 / ED ガード（w13 §8） ---

    [Fact]
    public async Task News_GeneratedPerOccurrence_TwiceForN4()
    {
        // N=4: 本編は talk,talk,letter,news を 2 周（news は index 5 と 9 の 2 回出現）。
        // s11 の「1 回だけ生成」から「出現のたびに生成」へ（w13 §3）。退行防止の固定。
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(4)), MakeThemes(), Corners, Djs);

        Assert.Equal(2, h.NewsCalls());
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    [Fact]
    public async Task RollingPreparation_DoesNotPrepareEverythingUpfront()
    {
        // Mac `rollingWindowDoesNotPrepareEverythingUpfront` 相当。N=10（コーナー 15 本）でも、冒頭曲の時点で
        // ED → 起動済みのトーク準備は窓ぶん（≤ PreparationWindow+1=3）に収まる（全先行しない）。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var llm = new RoutingLLM("アイドル - YOASOBI", ScriptResponse, LetterResponse);
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: llm, onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentStarted { Index: 1, Kind: SegmentKind.Song })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(10)), MakeThemes(), Corners, Djs, control);

        Assert.True(llm.ScriptCalls <= BroadcastEngine.PreparationWindow + 1,
            $"トーク準備が窓を超えて先行している: {llm.ScriptCalls}");
    }

    [Fact]
    public async Task RollingPreparation_Endless_DoesNotAccumulateUnbounded()
    {
        // エンドレスでも準備は窓ぶんに収まる（全先行なら EnsurePrepared が無限ループ＝完走しないはず）。
        // 完走 + ScriptCalls ≤ 3 で「際限なく積み上がらない（窓 k+2 以下）」を保証（w13 §8）。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var llm = new RoutingLLM("アイドル - YOASOBI", ScriptResponse, LetterResponse);
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: llm, onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentStarted { Index: 1, Kind: SegmentKind.Song })
            {
                control.RequestEnding();
            }
        });

        await h.Engine.RunAsync(MakePlan(ProgramLength.Endless), MakeThemes(), Corners, Djs, control);

        Assert.True(llm.ScriptCalls <= BroadcastEngine.PreparationWindow + 1,
            $"エンドレスで準備が窓を超えて先行している: {llm.ScriptCalls}");
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    [Fact]
    public async Task Ending_RequestedDuringNaturalEnding_DoesNotDoubleFire()
    {
        // 自然 ED 到達時（現セグメント = Ending）に ED 要求しても、ガード（Kind != Ending）で再発火・二重 ED しない。
        var spotify = new FakeSpotifyController();
        var control = new BroadcastControl();
        var h = BuildEngine(spotify, new FakeClock(), onEvent: e =>
        {
            if (e is BroadcastEvent.SegmentStarted { Kind: SegmentKind.Ending })
            {
                control.RequestEnding();
            }
        });

        // N=1: OP → song → talk → ED。ED セグメント実行中に ED 要求。
        await h.Engine.RunAsync(MakePlan(ProgramLength.FromCorners(1)), MakeThemes(), Corners, Djs, control);

        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.EndingRequested); // ガードで発火しない
        Assert.Equal(1, Plays(spotify).Count(u => u == EdTrack));                  // ED は 1 回だけ（二重なし）
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
    }

    // --- 失敗注入 / トレース / ルーティング用 fake（いずれも既存 interface の実装。新 interface を作らない）。 ---

    /// <summary>
    /// プロンプト内容で応答を振り分ける決定論 LLM（候補 / 会話台本 / 架空お便り）。並行準備でも安定。
    /// <see cref="ScriptCalls"/> は会話台本生成回数＝コーナー準備完了数の目安（ローリング窓上限の検証用）。
    /// </summary>
    private sealed class RoutingLLM(string candidates, string script, string letter) : ILLMBackend
    {
        private int _scriptCalls;
        public int ScriptCalls => Volatile.Read(ref _scriptCalls);

        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var p = request.Prompt;
            if (p.Contains("候補")) { return Task.FromResult(candidates); }
            if (p.Contains("会話台本")) { Interlocked.Increment(ref _scriptCalls); return Task.FromResult(script); }
            return Task.FromResult(letter); // 架空のリスナーからのお便り生成。
        }
    }

    /// <summary>候補生成（選曲）でキャンセルされるまでブロックする LLM（ED ③ の「未準備トーク」再現用）。</summary>
    private sealed class BlockingCandidatesLLM : ILLMBackend
    {
        private int _cancelled;
        public bool SawCancellation => Volatile.Read(ref _cancelled) > 0;

        public async Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            if (request.Prompt.Contains("候補"))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelled);
                    throw;
                }
            }
            return ScriptResponse; // 候補でブロックするため到達しない。
        }
    }

    /// <summary>指定 URI の再生で例外を投げ、それ以外は内部 fake へ委譲する。</summary>
    private sealed class ThrowOnPlaySpotify(string failUri, Exception ex) : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();

        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;

        public Task PlayAsync(string uri, CancellationToken ct = default)
            => uri == failUri ? throw ex : _inner.PlayAsync(uri, ct);

        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => _inner.SetVolumeAsync(percent, ct);
        public Task SeekAsync(int seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default) => _inner.PlayerStateAsync(ct);
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => _inner.CurrentTrackDurationSecondsAsync(ct);
    }

    /// <summary>指定 URI の再生で先に CTS を取り消してからドメインエラーを投げる（キャンセルのラップ再現）。</summary>
    private sealed class CancelThenThrowSpotify(CancellationTokenSource cts, string failUri) : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();

        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;

        public Task PlayAsync(string uri, CancellationToken ct = default)
        {
            if (uri == failUri)
            {
                cts.Cancel();
                throw SpotifyException.AuthFailed("request cancelled");
            }
            return _inner.PlayAsync(uri, ct);
        }

        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => _inner.SetVolumeAsync(percent, ct);
        public Task SeekAsync(int seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default) => _inner.PlayerStateAsync(ct);
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => _inner.CurrentTrackDurationSecondsAsync(ct);
    }

    /// <summary>再生を共有トレースに記録する Spotify（呼び出し順の検証用）。</summary>
    private sealed class TracingSpotify(List<string> trace) : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();

        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;

        public Task PlayAsync(string uri, CancellationToken ct = default)
        {
            lock (trace) { trace.Add($"play:{uri}"); }
            return _inner.PlayAsync(uri, ct);
        }

        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => _inner.SetVolumeAsync(percent, ct);
        public Task SeekAsync(int seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default) => _inner.PlayerStateAsync(ct);
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => _inner.CurrentTrackDurationSecondsAsync(ct);
    }

    /// <summary>LLM 呼び出しを共有トレースに記録して内部 fake へ委譲する。</summary>
    private sealed class TracingLLM(ILLMBackend inner, List<string> trace) : ILLMBackend
    {
        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            lock (trace) { trace.Add("talk-llm"); }
            return inner.GenerateAsync(request, ct);
        }
    }
}
