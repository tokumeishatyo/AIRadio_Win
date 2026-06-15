using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W7 BroadcastEngine の進行契約。具象 <see cref="ThemeSequencer"/> / <see cref="CornerEngine"/> へ既存 interface の
/// fake を注入し（新 interface を作らない, §3-5）、失敗は throwing fake（既存 interface の実装）で注入する。
/// </summary>
public class BroadcastEngineTests
{
    private const string OpTrack = "spotify:track:OP";
    private const string NewsTrack = "spotify:track:NEWS";
    private const string EdTrack = "spotify:track:ED";
    private const string IdolTrack = "spotify:track:idol";

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

    private static ProgramFormat MakeFormat() => new(
        "ケイラボAIラジオ", "zundamon",
        new[]
        {
            new ProgramSegment(SegmentKind.Opening, Critical: true),
            new ProgramSegment(SegmentKind.Talk, "free_talk"),
            new ProgramSegment(SegmentKind.News),
            new ProgramSegment(SegmentKind.Ending),
        });

    private static BroadcastThemes MakeThemes() => new(
        Opening: new ThemeConfig("OPタグ", OpTrack, IntroSeconds: 5, Volume: 100, DuckedVolume: 35, OutroSeconds: 10),
        OpeningAnnouncement: "オープニングです。",
        News: new ThemeConfig("ニュースです", NewsTrack, 5, 100, 35, 10),
        Ending: new ThemeConfig(null, EdTrack, 5, 100, 35, 10),
        EndingAnnouncement: "エンディングです。");

    private sealed record Harness(
        BroadcastEngine Engine, List<BroadcastEvent> Events, SpyAudioPlayer Audio, Func<int> NewsCalls);

    private static Harness BuildEngine(
        ISpotifyController spotify,
        ILLMBackend llm,
        IClock clock,
        Func<CancellationToken, Task<string>>? news = null)
    {
        var events = new List<BroadcastEvent>();
        var audio = new SpyAudioPlayer();
        var tts = new InMemoryTTS();
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo(IdolTrack, "アイドル", "YOASOBI", IsPlayable: true),
        });

        var newsCalls = new int[1];
        var baseNews = news ?? (_ => Task.FromResult("ニュース原稿"));
        Func<CancellationToken, Task<string>> countedNews = token =>
        {
            lock (newsCalls) { newsCalls[0]++; }
            return baseNews(token);
        };

        var themeSequencer = new ThemeSequencer(tts, audio, spotify, clock);
        var corner = new CornerEngine(llm, tts, audio, searcher, spotify, clock);
        var engine = new BroadcastEngine(
            themeSequencer, corner, countedNews, spotify,
            e => { lock (events) { events.Add(e); } });

        return new Harness(engine, events, audio, () => { lock (newsCalls) { return newsCalls[0]; } });
    }

    [Fact]
    public async Task HappyPath_RunsSegmentsInOrder_ThenFinishesAndPauses()
    {
        var spotify = new FakeSpotifyController();
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var h = BuildEngine(spotify, llm, new FakeClock());

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

        // セグメント順に Play: OP テーマ → トークの締め曲 → news テーマ → ED テーマ。
        var plays = spotify.Events.OfType<SpotifyEvent.Play>().Select(p => p.Uri).ToList();
        Assert.Equal(new[] { OpTrack, IdolTrack, NewsTrack, EdTrack }, plays);

        Assert.Equal(1, h.NewsCalls()); // ニュース原稿は news セグメントで 1 回消費。
        Assert.Equal(new BroadcastEvent.SegmentStarted(0, SegmentKind.Opening), h.Events[0]);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new BroadcastEvent.SegmentFinished(3, SegmentKind.Ending), h.Events);
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentFailed);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // 完全静寂（§3-1）。
    }

    [Fact]
    public async Task NewsSegment_FeedsAnchorReadScript_ToNewsTheme()
    {
        var spotify = new FakeSpotifyController();
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var h = BuildEngine(spotify, llm, new FakeClock(), news: _ => Task.FromResult("ニュース速報テスト"));

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs);

        // news 原稿は anchor（ずんだもん=speaker 3）が読む。InMemoryTTS は "{speakerId}:{text}" を返す。
        var spoken = h.Audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains(spoken, s => s.StartsWith("3:") && s.Contains("ニュース速報テスト"));
    }

    [Fact]
    public async Task TalkSegmentFails_FailTolerant_SkipsAndContinues()
    {
        var spotify = new FakeSpotifyController();
        // 選曲は成功、台本生成で応答が尽きて EmptyResponse → トークだけ失敗。
        var llm = new ScriptedLLM("アイドル - YOASOBI");
        var h = BuildEngine(spotify, llm, new FakeClock());

        await h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs); // 非 critical なので throw しない。

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 1 && f.Kind == SegmentKind.Talk && f.Code == "E-LLM-EMPTY-RESPONSE-001");
        // ニュース・ED は実行され、最後まで完走する。
        Assert.Equal(1, h.NewsCalls());
        Assert.Contains(new BroadcastEvent.SegmentFinished(3, SegmentKind.Ending), h.Events);
        Assert.Equal(new BroadcastEvent.BroadcastFinished(), h.Events[^1]);
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);
        // 失敗したトークは曲を流していない（準備段階で失敗）。
        Assert.DoesNotContain(spotify.Events.OfType<SpotifyEvent.Play>(), p => p.Uri == IdolTrack);
    }

    [Fact]
    public async Task CriticalOpeningFails_AbortsBroadcast_AndPauses()
    {
        var spotify = new ThrowOnPlaySpotify(OpTrack, SpotifyException.AuthFailed("token expired"));
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var h = BuildEngine(spotify, llm, new FakeClock());

        await Assert.ThrowsAsync<BroadcastException>(
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs));

        Assert.Contains(h.Events, e => e is BroadcastEvent.SegmentFailed f
            && f.Index == 0 && f.Kind == SegmentKind.Opening && f.Code == "E-SPT-AUTH-FAILED-001");
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.BroadcastFinished); // 中止
        Assert.DoesNotContain(h.Events, e => e is BroadcastEvent.SegmentStarted s && s.Kind == SegmentKind.Talk);
        Assert.Equal(0, h.NewsCalls()); // news に到達しない
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // §3-1
    }

    [Fact]
    public async Task PreCancelled_ThrowsImmediately_NoSegments_StillPauses()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new ScriptedLLM(), new FakeClock());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Corners, Djs, cts.Token));

        Assert.Empty(h.Events); // ループ先頭で throw、SegmentStarted も出ない
        Assert.Equal(0, h.NewsCalls());
        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events); // エンジンの後始末 pause
    }

    [Fact]
    public async Task CancellationWrappedAsDomainError_RethrownAsCancellation_NotSegmentFailed()
    {
        using var cts = new CancellationTokenSource();
        // OP 再生時に ct を取り消してからドメインエラーを投げる（取消された HttpClient のラップを再現）。
        var spotify = new CancelThenThrowSpotify(cts, OpTrack);
        var h = BuildEngine(spotify, new ScriptedLLM("アイドル - YOASOBI", ScriptResponse), new FakeClock());

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
        var h = BuildEngine(spotify, new ScriptedLLM(), new FakeClock());
        var format = MakeFormat() with { AnchorDjId = "ghost" };

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(format, MakeThemes(), Corners, Djs));

        Assert.Empty(spotify.Events); // 音を出す前に fail-fast
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task Preflight_UnknownTalkCorner_FailsFast_NoAudio()
    {
        var spotify = new FakeSpotifyController();
        var h = BuildEngine(spotify, new ScriptedLLM(), new FakeClock());

        await Assert.ThrowsAsync<ConfigException>(
            () => h.Engine.RunAsync(MakeFormat(), MakeThemes(), Array.Empty<CornerTemplate>(), Djs));

        Assert.Empty(spotify.Events);
        Assert.Empty(h.Events);
    }

    // --- 失敗注入用 fake（いずれも既存 ISpotifyController の実装。新 interface を作らない）。 ---

    /// <summary>指定 URI の再生で例外を投げ、それ以外は内部 fake へ委譲する。</summary>
    private sealed class ThrowOnPlaySpotify : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();
        private readonly string _failUri;
        private readonly Exception _ex;

        public ThrowOnPlaySpotify(string failUri, Exception ex) => (_failUri, _ex) = (failUri, ex);

        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;

        public Task PlayAsync(string uri, CancellationToken ct = default)
            => uri == _failUri ? throw _ex : _inner.PlayAsync(uri, ct);

        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => _inner.SetVolumeAsync(percent, ct);
        public Task SeekAsync(int seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default) => _inner.PlayerStateAsync(ct);
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => _inner.CurrentTrackDurationSecondsAsync(ct);
    }

    /// <summary>指定 URI の再生で先に CTS を取り消してからドメインエラーを投げる（キャンセルのラップ再現）。</summary>
    private sealed class CancelThenThrowSpotify : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();
        private readonly CancellationTokenSource _cts;
        private readonly string _failUri;

        public CancelThenThrowSpotify(CancellationTokenSource cts, string failUri) => (_cts, _failUri) = (cts, failUri);

        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;

        public Task PlayAsync(string uri, CancellationToken ct = default)
        {
            if (uri == _failUri)
            {
                _cts.Cancel();
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
}
