using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>実時刻を返す IClock 実装。</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public async Task DelayAsync(double seconds, CancellationToken ct = default)
    {
        if (seconds <= 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }
}
