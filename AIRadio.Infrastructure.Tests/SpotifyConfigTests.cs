using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class SpotifyConfigTests
{
    [Fact]
    public void FromYaml_Parses_AndDerivesLoopbackPort()
    {
        var yaml =
            "spotify:\n" +
            "  client_id: \"CID\"\n" +
            "  redirect_uri: \"http://127.0.0.1:5543/callback\"\n" +
            "  market: \"JP\"\n";

        var cfg = SpotifyConfig.FromYaml(yaml);

        Assert.Equal("CID", cfg.ClientId);
        Assert.Equal("JP", cfg.Market);
        Assert.Equal((ushort)5543, cfg.LoopbackPort);
    }

    [Fact]
    public void FromYaml_AppliesDefaults()
    {
        var cfg = SpotifyConfig.FromYaml("spotify:\n  client_id: \"X\"\n");

        Assert.Equal("http://127.0.0.1:5543/callback", cfg.RedirectUri);
        Assert.Equal("JP", cfg.Market);
        Assert.Null(cfg.DeviceName);
    }

    [Fact]
    public void FromYaml_MissingClientId_ThrowsConfigException()
    {
        var ex = Assert.Throws<ConfigException>(() => SpotifyConfig.FromYaml("spotify:\n  market: \"JP\"\n"));
        Assert.Equal("E-CFG-MISSING-FIELD-001", ex.Code);
    }
}
