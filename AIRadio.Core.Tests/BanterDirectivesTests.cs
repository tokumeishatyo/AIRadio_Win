using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>W-DLG 掛け合いルール解決（純粋）。main 一致＋requires 全員在席で directive、それ以外は null（fail-soft）。</summary>
public class BanterDirectivesTests
{
    private static DjProfile Dj(string id) => new(id, id, 1, "");

    private static BanterDirectives Rules(params BanterRule[] rules) => new(rules);

    [Fact]
    public void Resolve_MainAndRequiresMatch_ReturnsDirective()
    {
        var d = Rules(new BanterRule("reimu", new[] { "marisa" }, "DIR"));
        Assert.Equal("DIR", d.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }

    [Fact]
    public void Resolve_MainMismatch_ReturnsNull()
    {
        var d = Rules(new BanterRule("reimu", new[] { "marisa" }, "DIR"));
        Assert.Null(d.Resolve(new[] { Dj("marisa"), Dj("reimu") })); // main=marisa
    }

    [Fact]
    public void Resolve_RequiresMissing_ReturnsNull()
    {
        var d = Rules(new BanterRule("reimu", new[] { "marisa" }, "DIR"));
        Assert.Null(d.Resolve(new[] { Dj("reimu"), Dj("zundamon") })); // marisa 不在
    }

    [Fact]
    public void Resolve_EmptyCast_ReturnsNull()
    {
        var d = Rules(new BanterRule("reimu", new[] { "marisa" }, "DIR"));
        Assert.Null(d.Resolve(Array.Empty<DjProfile>()));
    }

    [Fact]
    public void Resolve_NoRequires_MatchesOnMainOnly()
    {
        var d = Rules(new BanterRule("reimu", Array.Empty<string>(), "DIR"));
        Assert.Equal("DIR", d.Resolve(new[] { Dj("reimu") }));
    }

    [Fact]
    public void Resolve_MultipleRules_ReturnsFirstMatch()
    {
        var d = Rules(
            new BanterRule("reimu", new[] { "marisa" }, "FIRST"),
            new BanterRule("reimu", new[] { "marisa", "zundamon" }, "SECOND"));
        Assert.Equal("FIRST", d.Resolve(new[] { Dj("reimu"), Dj("marisa"), Dj("zundamon") }));
    }

    [Fact]
    public void Resolve_Empty_ReturnsNull()
    {
        Assert.Null(BanterDirectives.Empty.Resolve(new[] { Dj("reimu"), Dj("marisa") }));
    }
}
