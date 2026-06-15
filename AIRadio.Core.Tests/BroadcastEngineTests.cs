using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W7 BroadcastEngine の進行契約（OP → 冒頭曲 → トーク → ニュース → ED、ローリング先読み準備）。
/// 具象 <see cref="ThemeSequencer"/> / <see cref="CornerEngine"/> / <see cref="SongPicker"/> へ既存 interface の
/// fake を注入し（新 interface を作らない, §3-5）、失敗は throwing fake（既存 interface の実装）で注入する。
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

    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    private static readonly IReadOnlyList<CornerTemplate> Corners = new[]
    {
        new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fallback", Volume: 100, PlaySeconds: 5),
    };

    private static ProgramFormat MakeFormat(int songPlaySeconds = 30, string? newsDjId = null) => new(
        "ケイラボAIラジオ", "zundamon",
        new[]
        {
            new ProgramSegment(SegmentKind.Opening, Critical: true),
            new ProgramSegment(SegmentKind.Song, Song: new SongSegmentSpec(SongFallback, "幕開けの曲", 100, songPlaySeconds)),
            new ProgramSegment(SegmentKind.Talk, "free_talk"),
            new ProgramSegment(SegmentKind.News, DjId: newsDjId),
            new ProgramSegment(SegmentKind.Ending),
        });

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
        TimeZoneInfo? timeZone = null)
    {
        var events = new List<BroadcastEvent>();
        var audio = new SpyAudioPlayer();
        var tts = new InMemoryTTS();

        firstSongPicker ??= DefaultFirstSongPicker();
        talkLlm ??= new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
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
            e => { lock (events) { events.Add(e); } },
            timeZone);

        return new Harness(engine, events, audio, () => { lock (newsCalls) { return newsCalls[0]; } });
    }

    [Fact]
    public async Task HappyPath_RunsSegmentsInOrder_WithFirstSong_ThenFinishesAndPauses()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

        // セグメント順に Play: OP テーマ → 冒頭曲 → トークの締め曲 → news テーマ → ED テーマ。
        var plays = spotify.Events.OfType<SpotifyEvent.Play>().Select(p => p.Uri).ToList();
        Assert.Equal(new[] { OpTrack, FirstSongTrack, TalkSongTrack, NewsTrack, EdTrack }, plays);

        Assert.Equal(1, h.NewsCalls());
        Assert.Equal(new BroadcastEvent.SegmentStarted(0, SegmentKind.Opening), h.Events[0]);
        Assert.Contains(h.Events, e => e is BroadcastEvent.SongStarted s && s.Index == 1 && s.Track.Uri == FirstSongTrack);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new BroadcastEvent.SegmentFinished(4, SegmentKind.Ending), h.Events);
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentFailed);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // 完全静寂（§3-1）。
    }

    [Fact]
    public async Task Opening_ExpandsFirstSong_ReadByAnchor()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

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

        await h.Engine.RunAsync(MakeFormat(), themes, Corners, Djs);

        // anchor(ずんだもん=3) が、実時刻 + 冒頭曲の曲振りで読む（ゼロ埋めなし）。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s =>
            s.StartsWith("3:") && s.Contains("こんにちは。6月12日、午後3時。本日のアーティストで、「本日の曲」。"));
    }

    [Fact]
    public async Task News_ExpandsTimePlaceholders_TwoStage_ReadByAnchor()
    {
        // 二段展開: Provider 原稿に残った {hour12}/{minute} を、エンジンが発話直前に実時刻で展開する。
        // 0:05 UTC → {hour12}=0 / {minute}=5（午前午後なし・ゼロ埋めなし）。
        var spotify = new FakeSpotifyController();
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 9, 0, 5, 0, TimeSpan.Zero));
        var h = BuildEngine(
            spotify, clock, timeZone: TimeZoneInfo.Utc,
            news: _ => Task.FromResult("時刻は{hour12}時{minute}分になりました。ニュース速報。"));

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

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

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

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

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

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
        var djs = new[]
        {
            new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
            new DjProfile("metan", "四国めたん", 2, "上品な口調"),
            new DjProfile("ryusei", "青山龍星", 13, "ニュースキャスター"),
        };
        var h = BuildEngine(spotify, new FakeClock(), news: _ => Task.FromResult("ニュース速報テスト"));

        await h.Engine.RunAsync(MakeFormat(newsDjId: "ryusei"), MakeThemes(), Corners, djs);

        // ニュースは専任の ryusei(speaker 13) が読む（anchor=zundamon(3) ではない）。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.StartsWith("13:") && s.Contains("ニュース速報テスト"));
        Assert.DoesNotContain(spoken, s => s.StartsWith("3:") && s.Contains("ニュース速報テスト"));
    }

    [Fact]
    public async Task Preflight_UnknownNewsDj_FailsFast()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(MakeFormat(newsDjId: "ghost"), MakeThemes(), Corners, Djs));

        Assert.Empty(spotify.Events);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task FullPlaySong_WaitsForFinish_EmitsSongFinished()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());

        await h.Engine.RunAsync(MakeFormat(songPlaySeconds: 0), MakeThemes(), Corners, Djs);

        Assert.Contains(h.Events, e => e is BroadcastEvent.SongFinished s && s.Index == 1);
    }

    [Fact]
    public async Task TalkSegmentFails_FailTolerant_SkipsAndContinues()
    {
        var spotify = new FakeSpotifyController();
        // 締め曲の選曲は成功、台本生成で応答が尽きて EmptyResponse → トークだけ失敗。
        var talkLlm = new ScriptedLLM("アイドル - YOASOBI");
        var h = BuildEngine(spotify, new FakeClock(), talkLlm: talkLlm);

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs); // 非 critical なので throw しない。

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 2 && f.Kind == SegmentKind.Talk && f.Code == "E-LLM-EMPTY-RESPONSE-001");
        // 冒頭曲・ニュース・ED は実行され、最後まで完走する。
        Assert.Equal(1, h.NewsCalls());
        Assert.Contains(new BroadcastEvent.SegmentFinished(4, SegmentKind.Ending), h.Events);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);
        var plays = spotify.Events.OfType<SpotifyEvent.Play>().Select(p => p.Uri).ToList();
        Assert.Contains(FirstSongTrack, plays);            // 冒頭曲は流れている
        Assert.DoesNotContain(TalkSongTrack, plays);       // 失敗トークは締め曲を流していない
    }

    [Fact]
    public async Task CriticalOpeningFails_AbortsBroadcast_AndPauses()
    {
        var spotify = new ThrowOnPlaySpotify(OpTrack, SpotifyException.AuthFailed("token expired"));
        var h = BuildEngine(spotify, new FakeClock());

        await Assert.ThrowsAsync<BroadcastException>(
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs));

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 0 && f.Kind == SegmentKind.Opening && f.Code == "E-SPT-AUTH-FAILED-001");
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished);     // 中止
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentStarted s && s.Kind == SegmentKind.Song);
        Assert.Equal(0, h.NewsCalls());                                                  // news 準備に到達しない
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
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs, cts.Token));

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
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs, cts.Token));

        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentFailed); // 誤判定しない
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);
    }

    [Fact]
    public async Task Preflight_UnknownAnchorDj_FailsFast_NoAudio()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new FakeClock());
        var format = MakeFormat() with { AnchorDjId = "ghost" };

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(format, MakeThemes(), Corners, Djs));

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
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Array.Empty<CornerTemplate>(), Djs));

        Assert.Empty(spotify.Events);
        Assert.Empty(h.Events);
    }

    // --- 失敗注入 / トレース用 fake（いずれも既存 interface の実装。新 interface を作らない）。 ---

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
