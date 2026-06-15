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
}
