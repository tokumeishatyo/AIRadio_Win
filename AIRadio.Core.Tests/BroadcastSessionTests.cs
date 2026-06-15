using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class BroadcastSessionTests
{
    [Fact]
    public async Task Start_RunsBroadcast_ThenReturnsToIdle()
    {
        var states = new List<BroadcastSession.State>();
        var session = new BroadcastSession(s => { lock (states) { states.Add(s); } });
        var gate = new TaskCompletionSource();

        Assert.True(session.Start(_ => gate.Task));
        Assert.Equal(BroadcastSession.State.Broadcasting, session.CurrentState);

        gate.SetResult();
        await session.StopAndWaitAsync();

        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);
        lock (states)
        {
            Assert.Equal(
                new[] { BroadcastSession.State.Broadcasting, BroadcastSession.State.Idle }, states);
        }
    }

    [Fact]
    public async Task Start_WhileBroadcasting_IsRejected()
    {
        var session = new BroadcastSession();
        var gate = new TaskCompletionSource();

        Assert.True(session.Start(_ => gate.Task));
        Assert.False(session.Start(_ => Task.CompletedTask)); // 多重開始の拒否

        gate.SetResult();
        await session.StopAndWaitAsync();
    }

    [Fact]
    public async Task Stop_CancelsTheBroadcastToken()
    {
        var session = new BroadcastSession();
        var observedCancel = new TaskCompletionSource<bool>();
        session.Start(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                observedCancel.SetResult(true); // 放送が停止要求を観測した
                throw;
            }
        });

        session.Stop();

        Assert.True(await observedCancel.Task);
        await session.StopAndWaitAsync();
        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);
    }

    [Fact]
    public async Task CanRestart_AfterReturningToIdle()
    {
        var session = new BroadcastSession();

        var gate1 = new TaskCompletionSource();
        Assert.True(session.Start(_ => gate1.Task));
        gate1.SetResult();
        await session.StopAndWaitAsync();
        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);

        var gate2 = new TaskCompletionSource();
        Assert.True(session.Start(_ => gate2.Task)); // idle 復帰後は再開できる
        gate2.SetResult();
        await session.StopAndWaitAsync();
        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);
    }

    [Fact]
    public async Task BroadcastException_StillReturnsToIdle()
    {
        var session = new BroadcastSession();

        session.Start(_ => throw new InvalidOperationException("boom")); // 放送本体が投げても
        await session.StopAndWaitAsync();

        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState); // 必ず idle へ戻る
    }

    [Fact]
    public void Stop_BeforeStart_IsNoOp()
    {
        var states = new List<BroadcastSession.State>();
        var session = new BroadcastSession(s => { lock (states) { states.Add(s); } });

        session.Stop(); // 未開始でも例外を投げず、状態も変えない

        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);
        lock (states)
        {
            Assert.Empty(states);
        }
    }

    [Fact]
    public async Task StopAndWait_WhenIdle_ReturnsImmediately()
    {
        var states = new List<BroadcastSession.State>();
        var session = new BroadcastSession(s => { lock (states) { states.Add(s); } });

        await session.StopAndWaitAsync(); // 未開始/idle でも即座に完了し、状態遷移コールバックは発火しない

        Assert.Equal(BroadcastSession.State.Idle, session.CurrentState);
        lock (states)
        {
            Assert.Empty(states);
        }
    }
}
