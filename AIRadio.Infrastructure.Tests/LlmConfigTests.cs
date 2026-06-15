using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class LlmConfigTests
{
    private const string MainYaml =
        "llm:\n  provider: gemini\n  model: \"gemini-3.1-flash-lite\"\n" +
        "  endpoint: \"https://example.com/\"\n  temperature: 0.8\n";

    [Fact]
    public void Load_MainAndLocal_BuildsConfig()
    {
        const string local = "llm:\n  api_key: \"real-key-123\"\n";

        var config = LlmConfig.Load(MainYaml, local);

        Assert.Equal("gemini", config.Provider);
        Assert.Equal("gemini-3.1-flash-lite", config.Model);
        Assert.Equal("https://example.com/", config.Endpoint);
        Assert.Equal(0.8, config.Temperature);
        Assert.Equal("real-key-123", config.ApiKey);
    }

    [Fact]
    public void Load_NoLocalFile_ThrowsKeyMissing()
    {
        var ex = Assert.Throws<LlmException>(() => LlmConfig.Load(MainYaml, null));
        Assert.Equal("E-LLM-KEY-MISSING-001", ex.Code);
    }

    [Fact]
    public void Load_PlaceholderKey_ThrowsKeyMissing()
    {
        const string local = "llm:\n  api_key: \"PASTE-YOUR-GEMINI-API-KEY-HERE\"\n";

        var ex = Assert.Throws<LlmException>(() => LlmConfig.Load(MainYaml, local));
        Assert.Equal("E-LLM-KEY-MISSING-001", ex.Code);
    }

    [Fact]
    public void Load_MissingModel_ThrowsMissingField()
    {
        const string main = "llm:\n  provider: gemini\n";
        const string local = "llm:\n  api_key: \"real-key\"\n";

        var ex = Assert.Throws<ConfigException>(() => LlmConfig.Load(main, local));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }

    [Fact]
    public void Load_AppliesDefaults_WhenOptionalOmitted()
    {
        const string main = "llm:\n  model: \"m\"\n";
        const string local = "llm:\n  api_key: \"k\"\n";

        var config = LlmConfig.Load(main, local);

        Assert.Equal("gemini", config.Provider);
        Assert.Contains("generativelanguage.googleapis.com", config.Endpoint);
        Assert.Equal(0.9, config.Temperature);
    }
}
