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
