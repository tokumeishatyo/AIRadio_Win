using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class SpotifyUriTests
{
    [Theory]
    [InlineData("spotify:track:abc123", "abc123")]
    [InlineData("https://open.spotify.com/track/abc123?si=xyz", "abc123")]
    [InlineData("https://open.spotify.com/track/abc123", "abc123")]
    [InlineData("abc123", "abc123")]
    public void TrackId_Extracts(string raw, string expected)
        => Assert.Equal(expected, SpotifyUri.TrackId(raw));

    [Fact]
    public void NormalizeTrack_ProducesCanonicalUri()
        => Assert.Equal("spotify:track:abc123", SpotifyUri.NormalizeTrack("https://open.spotify.com/track/abc123?si=x"));
}
