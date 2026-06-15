using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class SystemClockTests
{
    [Fact]
    public async Task DelayAsync_Zero_CompletesImmediately()
    {
        await new SystemClock().DelayAsync(0);
    }

    [Fact]
    public void Now_IsNonDecreasing()
    {
        var clock = new SystemClock();
        var t1 = clock.Now;
        var t2 = clock.Now;
        Assert.True(t2 >= t1);
    }
}
