using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W15 アーティスト一覧生成（LLM 候補 → 去重 → 実在検証 → 原子的上書き）。失敗時はファイル不変・<see cref="ArtistGenException"/>。
/// </summary>
public class ArtistListGeneratorTests
{
    /// <summary>ラウンドごとに用意した応答を順に返す LLM。</summary>
    private sealed class SequencedLLM : ILLMBackend
    {
        private readonly Queue<string> _responses;
        public SequencedLLM(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string> GenerateAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "");
        }
    }

    /// <summary>既知の名前にだけ曲を返すカタログ（実在検証用）。</summary>
    private sealed class StubCatalog : IArtistCatalog
    {
        private readonly IReadOnlySet<string> _known;
        public StubCatalog(params string[] known) => _known = known.ToHashSet();

        public Task<IReadOnlyList<TrackInfo>> TopTracksAsync(string artistName, int limit, CancellationToken ct = default)
        {
            IReadOnlyList<TrackInfo> tracks = _known.Contains(artistName)
                ? new[] { new TrackInfo($"spotify:track:{artistName}", "曲", artistName) }
                : Array.Empty<TrackInfo>();
            return Task.FromResult(tracks);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"airadio-artists-{Guid.NewGuid():N}.yaml");

    [Fact]
    public async Task Generate_DedupsVerifiesAndWritesReadableYaml()
    {
        var path = TempPath();
        try
        {
            // 重複（米津玄師×2）・非実在（捨てる）・実在（採用）が混在。
            var llm = new SequencedLLM("米津玄師\n米津玄師\n非実在アーティスト\nあいみょん");
            var catalog = new StubCatalog("米津玄師", "あいみょん");
            var gen = new ArtistListGenerator(llm, catalog);

            var count = await gen.GenerateAsync(new ArtistGenConfig(GenrePrompt: "x", TargetCount: 2), path);

            Assert.Equal(2, count);
            // 書き出した yaml は ArtistsConfig で読み戻せ、去重・実在検証済み・連番 id。
            var loaded = ArtistsConfig.LoadFile(path);
            Assert.Equal(new[] { "米津玄師", "あいみょん" }, loaded.Select(a => a.Name));
            Assert.Equal("artist_001", loaded[0].Id);
            Assert.Equal("artist_002", loaded[1].Id);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public async Task Generate_AllUnverified_ThrowsArtistGen_FileUnchanged()
    {
        var path = TempPath();
        File.WriteAllText(path, "artists:\n  - id: keep_001\n    name: \"既存\"\n");
        try
        {
            var llm = new SequencedLLM("架空1\n架空2");
            var catalog = new StubCatalog();   // 何も実在しない → 全滅
            var gen = new ArtistListGenerator(llm, catalog);

            await Assert.ThrowsAsync<ArtistGenException>(
                () => gen.GenerateAsync(new ArtistGenConfig(GenrePrompt: "x", TargetCount: 5), path));

            // 失敗時はファイル不変（既存の内容が残る）。
            var loaded = ArtistsConfig.LoadFile(path);
            Assert.Single(loaded);
            Assert.Equal("既存", loaded[0].Name);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public async Task Generate_Cancellation_Propagates_NoFileWritten()
    {
        var path = TempPath();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var gen = new ArtistListGenerator(new SequencedLLM("x"), new StubCatalog("x"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gen.GenerateAsync(new ArtistGenConfig(TargetCount: 5), path, ct: cts.Token));

        Assert.False(File.Exists(path));   // 何も書かない
    }
}
