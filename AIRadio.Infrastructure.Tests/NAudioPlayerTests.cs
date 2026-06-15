using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class NAudioPlayerTests
{
    [Fact]
    public void Volume_IsClamped()
    {
        Assert.Equal(1.0f, new NAudioPlayer().Volume);
        Assert.Equal(0.65f, new NAudioPlayer(0.65f).Volume);
        Assert.Equal(1.0f, new NAudioPlayer(1.5f).Volume);
        Assert.Equal(0.0f, new NAudioPlayer(-0.5f).Volume);
    }

    [Fact]
    public async Task Play_InvalidWavData_ThrowsAudioException()
    {
        var player = new NAudioPlayer();

        var ex = await Assert.ThrowsAsync<AudioException>(
            () => player.PlayAsync(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }));
        Assert.Equal("E-RTM-AUDIO-PLAYBACK-001", ex.Code);
    }
}
