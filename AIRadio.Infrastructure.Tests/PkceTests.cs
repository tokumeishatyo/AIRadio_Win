using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class PkceTests
{
    // RFC 7636 Appendix B のテストベクタ
    [Fact]
    public void Challenge_MatchesRfc7636Vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge(verifier));
    }

    [Fact]
    public void Verifier_IsUrlSafe_AndLongEnough()
    {
        var verifier = Pkce.GenerateVerifier();
        Assert.True(verifier.Length >= 43);
        Assert.False(verifier.Contains('+'));
        Assert.False(verifier.Contains('/'));
        Assert.False(verifier.Contains('='));
    }
}
