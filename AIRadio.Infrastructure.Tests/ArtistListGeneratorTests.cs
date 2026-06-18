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

    // --- W19b: ParseEntries 逸脱耐性 / reading 付き生成・書き出し ---

    [Fact]
    public void ParseEntries_NameAndReading_SplitsOnTab()
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries("米津玄師\tヨネヅケンシ"));
        Assert.Equal("米津玄師", e.Name);
        Assert.Equal("ヨネヅケンシ", e.Reading);
    }

    [Fact]
    public void ParseEntries_NoTab_RescuesNameOnly()
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries("あいみょん"));
        Assert.Equal("あいみょん", e.Name);
        Assert.Null(e.Reading);
    }

    [Fact]
    public void ParseEntries_StripsBulletAndNumber()
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries("- 1. サザンオールスターズ\tサザンオールスターズ"));
        Assert.Equal("サザンオールスターズ", e.Name);
        Assert.Equal("サザンオールスターズ", e.Reading);
    }

    [Fact]
    public void ParseEntries_HalfWidthKatakanaReading_Normalized()
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries("A\tｱｲﾐｮﾝ"));
        Assert.Equal("A", e.Name);
        Assert.Equal("アイミョン", e.Reading);   // 半角カナ → NFKC で全角化
    }

    [Theory]
    [InlineData("X\tえっくす")]   // ひらがな
    [InlineData("Y\tYomi")]       // 英字
    [InlineData("Z\t不明")]       // 漢字
    public void ParseEntries_NonKatakanaReading_DroppedToNull(string line)
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries(line));
        Assert.NotEmpty(e.Name);
        Assert.Null(e.Reading);   // 非カタカナは捨てる（VOICEVOX へ送らない）
    }

    [Fact]
    public void ParseEntries_MultipleTabs_UsesSecondColumnOnly()
    {
        var e = Assert.Single(ArtistListGenerator.ParseEntries("name\tヨミ\textra"));
        Assert.Equal("name", e.Name);
        Assert.Equal("ヨミ", e.Reading);
    }

    [Fact]
    public void ParseEntries_EmptyName_Skipped()
    {
        Assert.Empty(ArtistListGenerator.ParseEntries("\tヨミ"));
    }

    [Fact]
    public void ParseEntries_Crlf_StripsCarriageReturn_BothColumns()
    {
        // 読み列末尾の \r（2 列）と名前列末尾の \r（タブ無し）の両方を除去する。
        var r = ArtistListGenerator.ParseEntries("name\tヨミ\r\nあいみょん\r");
        Assert.Equal(2, r.Count);
        Assert.Equal("name", r[0].Name);
        Assert.Equal("ヨミ", r[0].Reading);
        Assert.Equal("あいみょん", r[1].Name);
        Assert.Null(r[1].Reading);
    }

    [Fact]
    public async Task Generate_WithReadings_WritesReadingAndRescuesTabless()
    {
        var path = TempPath();
        try
        {
            // 1 件目に読み付き・2 件目はタブ無し（名前のみ救済＝退行なし）。
            var llm = new SequencedLLM("米津玄師\tヨネヅケンシ\nあいみょん");
            var catalog = new StubCatalog("米津玄師", "あいみょん");
            var gen = new ArtistListGenerator(llm, catalog);

            var count = await gen.GenerateAsync(new ArtistGenConfig(GenrePrompt: "x", TargetCount: 2), path);

            Assert.Equal(2, count);
            var loaded = ArtistsConfig.LoadFile(path);
            Assert.Equal(new[] { "米津玄師", "あいみょん" }, loaded.Select(a => a.Name));
            Assert.Equal("ヨネヅケンシ", loaded[0].Reading);
            Assert.Null(loaded[1].Reading);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public void MakeRequest_PromptDeclaresTabFormatReadingExampleAndKatakana()
    {
        var r = ArtistListGenerator.MakeRequest(
            new ArtistGenConfig(GenrePrompt: "x", TargetCount: 5), want: 5, Array.Empty<string>(), 1.0);

        Assert.Contains("# 出力形式", r.Prompt);
        Assert.Contains("タブ区切り", r.Prompt);
        Assert.Contains("米津玄師\tヨネヅケンシ", r.Prompt);   // 読み例（タブ区切り）
        Assert.Contains("全角カタカナの読み", r.Prompt);        // 全角カタカナ指定
    }

    [Fact]
    public void Write_EmitsReadingWhenPresent_OmitsWhenNull()
    {
        var path = TempPath();
        try
        {
            ArtistListGenerator.Write(new[]
            {
                new ArtistProfile("artist_001", "米津玄師", "ヨネヅケンシ"),
                new ArtistProfile("artist_002", "あいみょん"),   // reading=null
            }, path);

            // 生成 YAML テキストを直接検査（OmitNull が効いていること。往復だけでは出力有無を検証できない）。
            var text = File.ReadAllText(path);
            Assert.Contains("ヨネヅケンシ", text);
            Assert.Equal(1, text.Split("reading:").Length - 1);   // reading: キーは非 null の 1 件だけ

            // 往復は Name 健全性・非 null reading 保持の併用チェック。
            var loaded = ArtistsConfig.LoadFile(path);
            Assert.Equal("ヨネヅケンシ", loaded[0].Reading);
            Assert.Null(loaded[1].Reading);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
