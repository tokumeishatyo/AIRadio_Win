using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class TemplateExpanderTests
{
    [Fact]
    public void Expand_ReplacesKnownKeys()
    {
        var result = TemplateExpander.Expand(
            "{dj}の ケイラボAIラジオ",
            new Dictionary<string, string> { ["dj"] = "ずんだもん" });

        Assert.Equal("ずんだもんの ケイラボAIラジオ", result);
    }

    [Fact]
    public void Expand_LeavesUnknownPlaceholdersIntact()
    {
        var result = TemplateExpander.Expand(
            "{a} と {b}",
            new Dictionary<string, string> { ["a"] = "X" });

        Assert.Equal("X と {b}", result);
    }

    [Fact]
    public void Expand_NoPlaceholders_ReturnsOriginal()
    {
        var result = TemplateExpander.Expand("そのまま", new Dictionary<string, string>());

        Assert.Equal("そのまま", result);
    }
}
