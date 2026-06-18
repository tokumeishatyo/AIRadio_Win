using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class ErrorsTests
{
    [Fact]
    public void SpotifyException_NoDevice_HasStableCode()
        => Assert.Equal("E-SPT-NO-DEVICE-001", SpotifyException.NoDevice().Code);

    [Fact]
    public void SpotifyException_ApiFailed_HasStableCode()
        => Assert.Equal("E-SPT-API-FAILED-001", SpotifyException.ApiFailed("boom").Code);

    [Fact]
    public void ConfigException_MissingField_HasStableCode()
        => Assert.Equal("E-CFG-MISSING-FIELD-001", ConfigException.MissingField("api_key").Code);

    [Fact]
    public void RadioException_IsThrowable_AndCarriesCodeAndMessage()
    {
        Action act = () => throw SpotifyException.NoDevice();
        var ex = Assert.Throws<SpotifyException>(act);

        Assert.IsAssignableFrom<RadioException>(ex);
        Assert.IsAssignableFrom<IRadioError>(ex);
        Assert.Equal("E-SPT-NO-DEVICE-001", ex.Code);
        Assert.Contains("Spotify", ex.Message);
    }

    [Fact]
    public void TtsException_Unreachable_HasStableCode()
        => Assert.Equal("E-TTS-UNREACHABLE-001", TtsException.Unreachable().Code);

    [Fact]
    public void TtsException_SynthesisFailed_HasStableCode()
        => Assert.Equal("E-TTS-SYNTHESIS-FAILED-001", TtsException.SynthesisFailed("x").Code);

    [Fact]
    public void AudioException_PlaybackFailed_HasStableCode()
        => Assert.Equal("E-RTM-AUDIO-PLAYBACK-001", AudioException.PlaybackFailed().Code);

    [Fact]
    public void SpotifyException_AuthFailed_HasStableCode()
        => Assert.Equal("E-SPT-AUTH-FAILED-001", SpotifyException.AuthFailed("x").Code);

    [Fact]
    public void SpotifyException_AuthRequired_HasStableCode()
        => Assert.Equal("E-SPT-AUTH-REQUIRED-001", SpotifyException.AuthRequired().Code);

    [Fact]
    public void SpotifyException_SearchFailed_HasStableCode()
        => Assert.Equal("E-SPT-SEARCH-FAILED-001", SpotifyException.SearchFailed("x").Code);

    // --- W15: ART は throw しない定数＋reason ビルダ（§11/§18-27。spec §12/§15 が ErrorsTests での検証を明示要求）。 ---

    [Fact]
    public void ArtistFeatureErrors_EmptyPool_HasStableCode()
        => Assert.Equal("E-ART-EMPTY-POOL-001", ArtistFeatureErrors.EmptyPool);

    [Fact]
    public void ArtistFeatureErrors_InsufficientTracks_HasStableCode()
        => Assert.Equal("E-ART-INSUFFICIENT-TRACKS-001", ArtistFeatureErrors.InsufficientTracks);

    [Fact]
    public void ArtistFeatureErrors_EmptyPoolReason_ContainsCode()
        => Assert.Contains(ArtistFeatureErrors.EmptyPool, ArtistFeatureErrors.EmptyPoolReason());

    [Fact]
    public void ArtistFeatureErrors_InsufficientTracksReason_ContainsCodeAndCounts()
    {
        var reason = ArtistFeatureErrors.InsufficientTracksReason(2, 3);
        Assert.Contains(ArtistFeatureErrors.InsufficientTracks, reason);
        Assert.Contains("2", reason);
        Assert.Contains("3", reason);
    }
}
