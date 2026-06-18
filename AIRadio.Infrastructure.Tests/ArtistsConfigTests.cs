using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W15 アーティストプール（<c>artists.yaml</c>）ローダ。空・未生成・コメントのみは正常（空リスト）、
/// 壊れ（必須欠落・重複）は throw。<b>ゲスト（空は throw）と逆の契約</b>（§18-3）。
/// </summary>
public class ArtistsConfigTests
{
    [Fact]
    public void Artists_FromYaml_LoadsProfiles()
    {
        const string yaml =
            "artists:\n  - id: artist_001\n    name: \"米津玄師\"\n  - id: artist_002\n    name: \"あいみょん\"\n";

        var artists = ArtistsConfig.FromYaml(yaml);

        Assert.Equal(2, artists.Count);
        Assert.Equal(new ArtistProfile("artist_001", "米津玄師"), artists[0]);
        Assert.Equal("あいみょん", artists[1].Name);
    }

    [Theory]
    [InlineData("artists: []\n")]        // 空リスト
    [InlineData("# コメントのみ\n")]      // コメントのみ
    [InlineData("\n   \n")]              // 空白のみ
    public void Artists_EmptyOrCommentOnly_ReturnsEmpty_NoThrow(string yaml)
    {
        Assert.Empty(ArtistsConfig.FromYaml(yaml));
    }

    [Theory]
    [InlineData("artists:\n  - name: \"x\"\n")]                                              // id 欠落
    [InlineData("artists:\n  - id: a\n")]                                                    // name 欠落
    [InlineData("artists:\n  - id: a\n    name: \"x\"\n  - id: a\n    name: \"y\"\n")]        // id 重複
    [InlineData("artists:\n  - id: a\n    name: \"米津玄師\"\n  - id: b\n    name: \"米津玄師\"\n")] // 正規化 name 重複
    public void Artists_Broken_ThrowsMissingField(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => ArtistsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Artists_LoadFile_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airadio-artists-missing-{Guid.NewGuid():N}.yaml");
        Assert.False(File.Exists(path));
        Assert.Empty(ArtistsConfig.LoadFile(path));
    }

    [Fact]
    public void Artists_Reading_LoadedWhenPresent_NullWhenAbsent()
    {
        // W19b: reading は任意。欠落許容（既存 yaml はそのまま読める）。
        const string yaml =
            "artists:\n"
            + "  - id: a1\n    name: \"米津玄師\"\n    reading: \"ヨネヅケンシ\"\n"
            + "  - id: a2\n    name: \"あいみょん\"\n";

        var artists = ArtistsConfig.FromYaml(yaml);

        Assert.Equal("ヨネヅケンシ", artists[0].Reading);
        Assert.Null(artists[1].Reading);
    }
}
