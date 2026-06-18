using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W15 アーティスト生成設定（<c>artist-gen.yaml</c>）ローダ。欠落は既定、<c>target_count &lt; 1</c> は既定にクランプ。</summary>
public class ArtistGenConfigTests
{
    [Fact]
    public void ArtistGen_FromYaml_ReadsGenrePromptAndCount()
    {
        const string yaml = "generation:\n  genre_prompt: \"洋楽ロック\"\n  target_count: 50\n";

        var c = ArtistGenConfig.FromYaml(yaml);

        Assert.Equal("洋楽ロック", c.GenrePrompt);
        Assert.Equal(50, c.TargetCount);
    }

    [Fact]
    public void ArtistGen_Empty_UsesDefaults()
    {
        var c = ArtistGenConfig.FromYaml("");

        Assert.Equal(100, c.TargetCount);
        Assert.Contains("邦楽", c.GenrePrompt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ArtistGen_NonPositiveCount_ClampsToDefault(int count)
    {
        var c = ArtistGenConfig.FromYaml($"generation:\n  target_count: {count}\n");
        Assert.Equal(100, c.TargetCount);   // < 1 は既定 100 にクランプ（1 でも throw でもない）
    }

    [Fact]
    public void ArtistGen_EmptyGenrePrompt_UsesDefault()
    {
        const string yaml = "generation:\n  genre_prompt: \"\"\n  target_count: 30\n";

        var c = ArtistGenConfig.FromYaml(yaml);

        Assert.Contains("邦楽", c.GenrePrompt);   // 空文字は既定にフォールバック
        Assert.Equal(30, c.TargetCount);
    }

    [Fact]
    public void ArtistGen_LoadFile_MissingFile_UsesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airadio-gen-missing-{Guid.NewGuid():N}.yaml");
        var c = ArtistGenConfig.LoadFile(path);
        Assert.Equal(100, c.TargetCount);
    }
}
