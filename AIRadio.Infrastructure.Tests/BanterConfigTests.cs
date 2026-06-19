using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-DLG banter.yaml ローダ: ルール読み・requires 省略・malformed で fail-fast・ファイル不在で Empty（tolerant）。</summary>
public class BanterConfigTests
{
    private static DjProfile Dj(string id) => new(id, id, 1, "");

    [Fact]
    public void FromYaml_ParsesRules()
    {
        const string yaml =
            "banter:\n  - main: reimu\n    requires: [marisa]\n    directive: \"DIR\"\n";

        var d = BanterConfig.FromYaml(yaml);

        Assert.Equal("DIR", d.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }

    [Fact]
    public void FromYaml_RequiresOmitted_DefaultsEmpty_MatchesOnMain()
    {
        const string yaml = "banter:\n  - main: reimu\n    directive: \"DIR\"\n";

        var d = BanterConfig.FromYaml(yaml);

        Assert.Equal("DIR", d.Resolve(new[] { Dj("reimu") }));
    }

    [Fact]
    public void FromYaml_EmptyBanter_ReturnsEmpty()
    {
        var d = BanterConfig.FromYaml("banter: []\n");

        Assert.Null(d.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }

    [Fact]
    public void FromYaml_BanterNullValue_ReturnsEmpty()
    {
        // `banter: null`（または値なし）も注入なし扱い（tolerant）。
        var d = BanterConfig.FromYaml("banter: null\n");

        Assert.Null(d.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }

    [Fact]
    public void FromYaml_MultipleRules_ParsesAll_ResolvesFirstMatch()
    {
        const string yaml =
            "banter:\n" +
            "  - main: reimu\n    requires: [marisa]\n    directive: \"FIRST\"\n" +
            "  - main: reimu\n    requires: [marisa, zundamon]\n    directive: \"SECOND\"\n";

        var d = BanterConfig.FromYaml(yaml);

        Assert.Equal("FIRST", d.Resolve(new[] { Dj("reimu"), Dj("marisa"), Dj("zundamon") }));
    }

    [Fact]
    public void FromYaml_MissingMain_ThrowsMissingField()
    {
        var ex = Assert.Throws<ConfigException>(() => BanterConfig.FromYaml("banter:\n  - directive: \"DIR\"\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void FromYaml_MissingDirective_ThrowsMissingField()
    {
        var ex = Assert.Throws<ConfigException>(() => BanterConfig.FromYaml("banter:\n  - main: reimu\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void LoadFile_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"no-such-banter-{Guid.NewGuid():N}.yaml");

        var d = BanterConfig.LoadFile(path);

        Assert.Null(d.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }
}
