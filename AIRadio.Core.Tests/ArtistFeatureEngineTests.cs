using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W15 アーティスト特集（<see cref="ArtistFeatureEngine"/>）の決定論的契約。Mac <c>ArtistFeatureEngineTests</c> の移植。
/// 外部依存（LLM/TTS/Audio/Catalog/Spotify/Clock）は fake 差し替え（CLAUDE.md §4-4）。
/// </summary>
public class ArtistFeatureEngineTests
{
    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, ""),
        new DjProfile("metan", "四国めたん", 2, ""),
    };

    private static CornerTemplate FeatureCorner(int playSeconds = 1) => new(
        Id: "artist_feature", Title: "アーティスト特集", Theme: "x",
        Format: CornerFormat.ArtistFeature, DjIds: new[] { "zundamon", "metan" },
        FallbackTrackUri: "spotify:track:F", Volume: 100, PlaySeconds: playSeconds,
        LeadIn: "{ampm}{hour}時です。本日は{artist}さんを特集します。",
        ArtistFeatureParams: new ArtistFeatureParams(OutroLine: "以上、{artist}特集でした。"));

    private static List<TrackInfo> Tracks(int n) =>
        Enumerable.Range(1, n).Select(i => new TrackInfo($"spotify:track:T{i}", $"曲{i}", "米津玄師")).ToList();

    /// <summary>パースできる台本応答（メイン＋サブの 2 行）を n 個。</summary>
    private static string[] ValidResponses(int n) =>
        Enumerable.Repeat("ずんだもん: テストのセリフ\n四国めたん: 受けのセリフ", n).ToArray();

    private static int PlayCount(IReadOnlyList<SpotifyEvent> events) =>
        events.Count(e => e is SpotifyEvent.Play);

    [Fact]
    public void SplitGroups_3plus3plus1_AndReductions()
    {
        Assert.Equal(new[] { 3, 3, 1 }, ArtistFeatureEngine.SplitGroups(Tracks(7)).Select(g => g.Count));
        Assert.Equal(new[] { 3, 3 }, ArtistFeatureEngine.SplitGroups(Tracks(6)).Select(g => g.Count));
        Assert.Equal(new[] { 3, 2 }, ArtistFeatureEngine.SplitGroups(Tracks(5)).Select(g => g.Count));
        Assert.Equal(new[] { 3, 1 }, ArtistFeatureEngine.SplitGroups(Tracks(4)).Select(g => g.Count));
        Assert.Equal(new[] { 3 }, ArtistFeatureEngine.SplitGroups(Tracks(3)).Select(g => g.Count));
    }

    [Fact]
    public void Deduplicate_RemovesSameUriAndNormalizedTitle()
    {
        var input = new[]
        {
            new TrackInfo("u1", "曲A", "x"),
            new TrackInfo("u1", "曲A2", "x"),              // 同 URI → 除外
            new TrackInfo("u2", "曲A (Remaster)", "x"),    // 正規化で 曲A と同じ → 除外
            new TrackInfo("u3", "曲B", "x"),
        };
        Assert.Equal(new[] { "u1", "u3" }, ArtistFeatureEngine.Deduplicate(input).Select(t => t.Uri));
    }

    [Fact]
    public async Task PrepareFull_K7_GroupsScriptsOutroAndArtistSubstitution()
    {
        var corner = FeatureCorner();
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(ValidResponses(6)),   // 導入1 + グループ紹介3 + 感想2
            new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(new Dictionary<string, IReadOnlyList<TrackInfo>> { ["米津玄師"] = Tracks(7) }),
            new FakeSpotifyController(), new FakeClock());

        var prepared = await engine.PrepareAsync(
            corner, new ArtistProfile("a", "米津玄師"), Djs, new[] { "zundamon", "metan" }, corner.LeadIn);

        Assert.False(prepared.Skipped);
        Assert.Equal(new[] { 3, 3, 1 }, prepared.Groups.Select(g => g.Count));
        Assert.Equal(3, prepared.GroupIntroScripts.Count);
        Assert.Equal(2, prepared.CommentScripts.Count);   // 最後のグループの後は締め＝感想なし
        Assert.Equal("以上、米津玄師特集でした。", prepared.OutroLine.Text);   // {artist} が準備時に実名へ置換される
        Assert.Contains("米津玄師", prepared.LeadIn);
        Assert.Equal(prepared.IntroScript.Lines.Count, prepared.IntroAudio.Count);
    }

    [Fact]
    public async Task Prepare_EmptyPool_Skips_WithEmptyPoolCode()
    {
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(), new FakeSpotifyController(), new FakeClock());

        var prepared = await engine.PrepareAsync(
            FeatureCorner(), artist: null, Djs, new[] { "zundamon", "metan" }, leadIn: null);

        Assert.True(prepared.Skipped);
        Assert.Contains("E-ART-EMPTY-POOL-001", prepared.SkipReason);
    }

    [Fact]
    public async Task Prepare_InsufficientTracks_Skips_WithInsufficientCode()
    {
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(new Dictionary<string, IReadOnlyList<TrackInfo>> { ["米津玄師"] = Tracks(2) }),
            new FakeSpotifyController(), new FakeClock());

        var prepared = await engine.PrepareAsync(
            FeatureCorner(), new ArtistProfile("a", "米津玄師"), Djs, new[] { "zundamon", "metan" }, leadIn: null);

        Assert.True(prepared.Skipped);
        Assert.Contains("E-ART-INSUFFICIENT-TRACKS-001", prepared.SkipReason);
    }

    [Fact]
    public async Task Run_K4_PlaysContinuously_OnePausePerGroupPlusTail_OutroLast()
    {
        var audio = new SpyAudioPlayer();
        var spotify = new FakeSpotifyController();
        var events = new List<ArtistFeatureEvent>();
        var corner = FeatureCorner(playSeconds: 1);
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(ValidResponses(4)),   // 導入1 + グループ紹介2 + 感想1
            new InMemoryTTS(), audio,
            new FakeArtistCatalog(new Dictionary<string, IReadOnlyList<TrackInfo>> { ["米津玄師"] = Tracks(4) }),
            spotify, new FakeClock(), onEvent: e => events.Add(e));

        var prepared = await engine.PrepareAsync(
            corner, new ArtistProfile("a", "米津玄師"), Djs, new[] { "zundamon", "metan" }, corner.LeadIn);
        await engine.RunAsync(prepared, Djs);

        Assert.Equal(4, PlayCount(spotify.Events));
        // 各グループの後に pause（[3,1] の 2 グループ）＋ run 末尾の pause = 3。
        Assert.Equal(3, spotify.Events.Count(e => e is SpotifyEvent.Pause));
        Assert.IsType<SpotifyEvent.Pause>(spotify.Events[^1]);
        // グループ1（3曲）は曲間に pause を挟まず連続: 最初の pause より前に play が 3 つ。
        var firstPause = spotify.Events.ToList().FindIndex(e => e is SpotifyEvent.Pause);
        var group1Plays = spotify.Events.Take(firstPause).Count(e => e is SpotifyEvent.Play);
        Assert.Equal(3, group1Plays);
        Assert.Equal(4, events.Count(e => e is ArtistFeatureEvent.SongStarted));
        Assert.DoesNotContain(events, e => e is ArtistFeatureEvent.FeatureSkipped);
        Assert.Equal(prepared.OutroAudio, audio.Played[^1]);
    }

    [Fact]
    public async Task Run_Skipped_EmitsFeatureSkipped_PlaysNothing()
    {
        var spotify = new FakeSpotifyController();
        var events = new List<ArtistFeatureEvent>();
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(), spotify, new FakeClock(), onEvent: e => events.Add(e));
        var prepared = PreparedArtistFeature.Skip(
            FeatureCorner(), new[] { "zundamon", "metan" }, "E-ART-EMPTY-POOL-001: x");

        await engine.RunAsync(prepared, Djs);

        Assert.Contains(events, e => e is ArtistFeatureEvent.FeatureSkipped);
        Assert.Empty(spotify.Events);
    }

    // --- 完全静寂（§3-1/§8-2・spec §12）: 連続再生中の例外/キャンセルでも必ず pause＋音量復元 ---

    [Fact]
    public async Task Run_ExceptionDuringPlayback_AlwaysPauses_AndRestoresVolume()
    {
        // 2 曲目（グループ内連続再生中）の再生で失敗 → catch が PauseIgnoringCancellationAsync(restoringVolume=100)。
        var spotify = new ThrowOnTrackSpotify("spotify:track:T2");
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(ValidResponses(4)), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(new Dictionary<string, IReadOnlyList<TrackInfo>> { ["米津玄師"] = Tracks(4) }),
            spotify, new FakeClock());
        var prepared = await engine.PrepareAsync(
            FeatureCorner(), new ArtistProfile("a", "米津玄師"), Djs, new[] { "zundamon", "metan" }, leadIn: null);

        await Assert.ThrowsAsync<SpotifyException>(() => engine.RunAsync(prepared, Djs));

        Assert.Contains(spotify.Events, e => e is SpotifyEvent.Pause);
        Assert.IsType<SpotifyEvent.SetVolume>(spotify.Events[^1]);                 // 後始末で音量復元
        Assert.Equal(100, ((SpotifyEvent.SetVolume)spotify.Events[^1]).Percent);   // restoringVolume = corner.Volume
    }

    [Theory]
    [InlineData("spotify:track:T1")]   // 1 曲目再生中
    [InlineData("spotify:track:T2")]   // 1→2 遷移直後
    [InlineData("spotify:track:T3")]   // 3 曲目再生中
    public async Task Run_CancellationDuringPlayback_Propagates_AndPauses(string cancelAtUri)
    {
        using var cts = new CancellationTokenSource();
        var spotify = new CancelOnTrackSpotify(cts, cancelAtUri);
        var engine = new ArtistFeatureEngine(
            new ScriptedLLM(ValidResponses(2)), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeArtistCatalog(new Dictionary<string, IReadOnlyList<TrackInfo>> { ["米津玄師"] = Tracks(3) }),
            spotify, new FakeClock());
        var prepared = await engine.PrepareAsync(
            FeatureCorner(), new ArtistProfile("a", "米津玄師"), Djs, new[] { "zundamon", "metan" }, leadIn: null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunAsync(prepared, Djs, cts.Token));

        Assert.Contains(spotify.Events, e => e is SpotifyEvent.Pause);   // 停止時に必ず無音化（§3-1）
    }

    /// <summary>指定 URI の再生で例外を投げ、それ以外と pause/volume は内部 fake へ委譲する。</summary>
    private sealed class ThrowOnTrackSpotify : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();
        private readonly string _failUri;
        public ThrowOnTrackSpotify(string failUri) => _failUri = failUri;
        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;
        public Task PlayAsync(string uri, CancellationToken ct = default)
            => uri == _failUri ? throw SpotifyException.ApiFailed("再生失敗") : _inner.PlayAsync(uri, ct);
        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task SetVolumeAsync(int percent, CancellationToken ct = default) => _inner.SetVolumeAsync(percent, ct);
        public Task SeekAsync(int seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public Task<PlayerState> PlayerStateAsync(CancellationToken ct = default) => _inner.PlayerStateAsync(ct);
        public Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
            => _inner.CurrentTrackDurationSecondsAsync(ct);
    }

    /// <summary>指定 URI の再生で CTS を取り消して OCE を投げる（キャンセル再現）。pause/volume は記録する。</summary>
    private sealed class CancelOnTrackSpotify : ISpotifyController
    {
        private readonly FakeSpotifyController _inner = new();
        private readonly CancellationTokenSource _cts;
        private readonly string _failUri;
        public CancelOnTrackSpotify(CancellationTokenSource cts, string failUri) { _cts = cts; _failUri = failUri; }
        public IReadOnlyList<SpotifyEvent> Events => _inner.Events;
        public Task PlayAsync(string uri, CancellationToken ct = default)
        {
            if (uri == _failUri)
            {
                _cts.Cancel();
                throw new OperationCanceledException(_cts.Token);
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
