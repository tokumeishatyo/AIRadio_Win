using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W19a 読み辞書（<c>pronunciations.yaml</c>）ローダ。空・未生成・コメントのみは正常（空リスト）、
/// surface/pronunciation 欠落・壊れは throw（fail-fast）。<c>accent_type</c> 欠落は既定 0。
/// </summary>
public class PronunciationsConfigTests
{
    [Fact]
    public void FromYaml_ParsesAllFields()
    {
        const string yaml =
            "pronunciations:\n" +
            "  - surface: \"Mr.Children\"\n" +
            "    pronunciation: \"ミスターチルドレン\"\n" +
            "    accent_type: 3\n" +
            "    word_type: \"PROPER_NOUN\"\n" +
            "    priority: 8\n";

        var e = Assert.Single(PronunciationsConfig.FromYaml(yaml));

        Assert.Equal("Mr.Children", e.Surface);
        Assert.Equal("ミスターチルドレン", e.Pronunciation);
        Assert.Equal(3, e.AccentType);
        Assert.Equal("PROPER_NOUN", e.WordType);
        Assert.Equal(8, e.Priority);
    }

    [Fact]
    public void FromYaml_AccentTypeDefaultsToZero_AndOptionalsNull()
    {
        const string yaml =
            "pronunciations:\n" +
            "  - surface: \"栄光の架橋\"\n" +
            "    pronunciation: \"エイコウノカケハシ\"\n";

        var e = Assert.Single(PronunciationsConfig.FromYaml(yaml));

        Assert.Equal(0, e.AccentType);
        Assert.Null(e.WordType);
        Assert.Null(e.Priority);
    }

    [Theory]
    [InlineData("")]                          // 空
    [InlineData("\n   \n")]                    // 空白のみ
    [InlineData("# コメントのみ\n# もう 1 行\n")] // コメントのみ
    [InlineData("pronunciations: []\n")]       // 空リスト
    [InlineData("other: 1\n")]                 // pronunciations キーなし
    public void FromYaml_EmptyOrNoEntries_ReturnsEmpty(string yaml)
    {
        Assert.Empty(PronunciationsConfig.FromYaml(yaml));
    }

    [Theory]
    [InlineData("pronunciations:\n  - pronunciation: \"ミスターチルドレン\"\n")]  // surface 欠落
    [InlineData("pronunciations:\n  - surface: \"Mr.Children\"\n")]              // pronunciation 欠落
    [InlineData("pronunciations: [unclosed\n")]                                   // 壊れ YAML
    public void FromYaml_Broken_ThrowsMissingField(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => PronunciationsConfig.FromYaml(yaml));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void LoadFile_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airadio-pron-missing-{Guid.NewGuid():N}.yaml");
        Assert.False(File.Exists(path));
        Assert.Empty(PronunciationsConfig.LoadFile(path));
    }
}
