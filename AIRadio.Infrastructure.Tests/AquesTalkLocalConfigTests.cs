using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-AQT ライセンスキー（aquestalk.local.yaml）の tolerant ローダ。</summary>
public class AquesTalkLocalConfigTests
{
    [Fact]
    public void FromYaml_ReadsThreeKeys()
    {
        const string yaml =
            "aquestalk_dev_key: \"DEV-1\"\n" +
            "aquestalk_usr_key: \"USR-1\"\n" +
            "aqkanji2koe_dev_key: \"K2K-1\"\n";

        var cfg = AquesTalkLocalConfig.FromYaml(yaml);

        Assert.Equal("DEV-1", cfg.AquesTalkDevKey);
        Assert.Equal("USR-1", cfg.AquesTalkUsrKey);
        Assert.Equal("K2K-1", cfg.AqKanji2KoeDevKey);
    }

    [Fact]
    public void FromYaml_MissingKeys_DefaultEmpty()
    {
        var cfg = AquesTalkLocalConfig.FromYaml("aquestalk_dev_key: \"DEV-1\"\n");

        Assert.Equal("DEV-1", cfg.AquesTalkDevKey);
        Assert.Equal("", cfg.AquesTalkUsrKey);
        Assert.Equal("", cfg.AqKanji2KoeDevKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment only\n")]
    public void FromYaml_EmptyOrCommentOnly_ReturnsEmpty(string yaml)
    {
        Assert.Equal(AquesTalkLocalConfig.Empty, AquesTalkLocalConfig.FromYaml(yaml));
    }

    [Fact]
    public void LoadFile_Absent_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aquestalk.local.absent.{Guid.NewGuid():N}.yaml");
        Assert.Equal(AquesTalkLocalConfig.Empty, AquesTalkLocalConfig.LoadFile(path));
    }
}
