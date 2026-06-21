using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

public class SongPickerTests
{
    [Fact]
    public void ParseCandidates_ExtractsTitleArtist_StripsBulletsAndNumbers()
    {
        const string raw =
            "1. アイドル - YOASOBI\n" +
            "- 群青 - YOASOBI\n" +
            "* Lemon - 米津玄師\n" +
            "これは形式外の行\n" +
            "  夜に駆ける - YOASOBI  ";

        var candidates = SongPicker.ParseCandidates(raw);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(("アイドル", "YOASOBI"), candidates[0]);
        Assert.Equal(("群青", "YOASOBI"), candidates[1]);
        Assert.Equal(("Lemon", "米津玄師"), candidates[2]);
        Assert.Equal(("夜に駆ける", "YOASOBI"), candidates[3]);
    }

    [Fact]
    public async Task Pick_ReturnsFirstPlayableCandidate()
    {
        var llm = new ScriptedLLM("ない曲 - 誰か\nアイドル - YOASOBI");
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"));

        Assert.Equal("spotify:track:idol", track.Uri);
    }

    [Fact]
    public async Task Pick_FallsBackWhenNoCandidatePlayable()
    {
        var llm = new ScriptedLLM("架空曲 - 架空アーティスト");
        var searcher = new FakeTrackSearcher(); // 検索結果なし
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"));

        Assert.Equal("spotify:track:fallback", track.Uri); // 全滅は fallback（曲名不明）
        Assert.Equal("", track.Title);
    }

    [Fact]
    public async Task Pick_SkipsCandidateWhoseSearchThrows_AndTriesNext()
    {
        // 候補単位の検索失敗は握り潰して次の候補へ（fail-tolerant）。
        var llm = new ScriptedLLM("壊れ曲 - X\nアイドル - YOASOBI");
        var searcher = new SequencedSearcher(
            () => throw new InvalidOperationException("search boom"),
            () => new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true) });
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"));

        Assert.Equal("spotify:track:idol", track.Uri); // 1 候補目失敗 → 2 候補目を採用
    }

    [Fact]
    public async Task Pick_PropagatesCancellation()
    {
        // 停止（キャンセル）は握り潰さず伝播させる（完全静寂 §3-1。fallback に倒さない）。
        var llm = new ScriptedLLM("曲 - アーティスト");
        var searcher = new SequencedSearcher(() => throw new OperationCanceledException());
        var picker = new SongPicker(llm, searcher);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback")));
    }

    // --- W-DEDUP: 演奏済みリングによる重複回避 ---

    [Fact]
    public async Task Pick_SkipsRecentCandidate_TakesNextUnplayed()
    {
        // 先頭候補が既出 → 次候補の未既出・再生可能曲を採る（TryReserve が FirstOrDefault の後段で採否を分ける）。
        var history = new PlayedSongHistory(100);
        history.TryReserve(new TrackInfo("spotify:track:old", "古い曲", "X"));   // あらかじめ既出にしておく
        var llm = new ScriptedLLM("古い曲 - X\n新しい曲 - Y");
        var searcher = new MappedSearcher(
            ("古い曲", new TrackInfo("spotify:track:old", "古い曲", "X", IsPlayable: true)),
            ("新しい曲", new TrackInfo("spotify:track:new", "新しい曲", "Y", IsPlayable: true)));
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"), history: history);

        Assert.Equal("spotify:track:new", track.Uri);   // 既出 old を飛ばし new を採用
    }

    [Fact]
    public async Task Pick_AllCandidatesRecent_FallsBack_FallbackNotRecorded()
    {
        // 全候補が既出 → fallback に倒す。fallback は TryReserve しない＝記録されない（IsRecent false）。
        var history = new PlayedSongHistory(100);
        history.TryReserve(new TrackInfo("spotify:track:a", "曲A", "X"));
        history.TryReserve(new TrackInfo("spotify:track:b", "曲B", "Y"));
        var llm = new ScriptedLLM("曲A - X\n曲B - Y");
        var searcher = new MappedSearcher(
            ("曲A", new TrackInfo("spotify:track:a", "曲A", "X", IsPlayable: true)),
            ("曲B", new TrackInfo("spotify:track:b", "曲B", "Y", IsPlayable: true)));
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"), history: history);

        Assert.Equal("spotify:track:fallback", track.Uri);
        Assert.Equal("", track.Title);
        Assert.False(history.IsRecent("spotify:track:fallback"));   // fallback は記録しない
    }

    [Fact]
    public async Task Pick_RecordsAcceptedTrack()
    {
        // 採用曲は TryReserve で記録される（次回 IsRecent true）。
        var history = new PlayedSongHistory(100);
        var llm = new ScriptedLLM("アイドル - YOASOBI");
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var picker = new SongPicker(llm, searcher);

        var track = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"), history: history);

        Assert.Equal("spotify:track:idol", track.Uri);
        Assert.True(history.IsRecent("spotify:track:idol"));
    }

    [Fact]
    public async Task Pick_NullHistory_LegacyBehavior_NoDedup()
    {
        // history 既定 null → 既出判定なし（従来どおり先頭再生可能曲を採る。同曲を二度採っても OK）。
        // ScriptedLLM は単一消費キューゆえ 2 回ぶんの候補応答を与える。
        var llm = new ScriptedLLM("アイドル - YOASOBI", "アイドル - YOASOBI");
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var picker = new SongPicker(llm, searcher);

        var track1 = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"));
        var track2 = await picker.PickAsync(new SongRequest("締めの曲", "spotify:track:fallback"));

        Assert.Equal("spotify:track:idol", track1.Uri);
        Assert.Equal("spotify:track:idol", track2.Uri);
    }

    /// <summary>クエリに含まれる title をキーに対応する 1 曲を返す検索 fake（候補ごとに別 URI を返し dedup の動線を作る）。</summary>
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

    /// <summary>SearchAsync 呼び出しごとに、用意した手順（結果を返す / 例外を投げる）を順に実行する検索 fake。</summary>
    private sealed class SequencedSearcher : ITrackSearcher
    {
        private readonly Queue<Func<IReadOnlyList<TrackInfo>>> _steps;

        public SequencedSearcher(params Func<IReadOnlyList<TrackInfo>>[] steps) => _steps = new(steps);

        public Task<IReadOnlyList<TrackInfo>> SearchAsync(string query, int limit, CancellationToken ct = default)
        {
            var step = _steps.Count > 0 ? _steps.Dequeue() : () => (IReadOnlyList<TrackInfo>)Array.Empty<TrackInfo>();
            return Task.FromResult(step()); // step() が例外を投げれば SearchAsync から同期的に伝播
        }

        public Task<bool> IsPlayableAsync(string uri, CancellationToken ct = default) => Task.FromResult(true);
    }
}
