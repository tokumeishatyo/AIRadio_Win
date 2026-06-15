namespace AIRadio.Core;

/// <summary>
/// 放送タスクのライフサイクル管理（トレイ UI から開始 / 停止する単位）。Mac 版 `actor BroadcastSession` の移植。
/// 放送全体を 1 つの <see cref="Task"/> で回し、停止は内蔵 <see cref="CancellationTokenSource"/> の Cancel（CLAUDE.md §3-1）。
/// actor の直列化は <c>lock</c> ガードで再現する。
/// </summary>
public sealed class BroadcastSession
{
    public enum State
    {
        Idle,
        Broadcasting,
    }

    private readonly object _lock = new();
    private readonly Action<State>? _onStateChange;
    private State _state = State.Idle;
    private Task? _task;
    private CancellationTokenSource? _cts;

    /// <param name="onStateChange">
    /// 状態遷移コールバック。<c>lock</c> 内から呼ばれるため**非ブロッキング**であること
    /// （UI 反映は <c>Dispatcher.UIThread.Post</c> 等でマーシャルする）。
    /// </param>
    public BroadcastSession(Action<State>? onStateChange = null) => _onStateChange = onStateChange;

    public State CurrentState
    {
        get { lock (_lock) { return _state; } }
    }

    /// <summary>
    /// 放送を開始する。すでに放送中なら何もせず false（多重開始の拒否）。
    /// <paramref name="broadcast"/> はキャンセルに応答し、正常・失敗いずれでも return すること
    /// （完了で自動的に <see cref="State.Idle"/> へ戻る）。
    /// </summary>
    public bool Start(Func<CancellationToken, Task> broadcast)
    {
        lock (_lock)
        {
            if (_state != State.Idle)
            {
                return false;
            }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            // Task は lock 内で生成・代入する。本体内の Finish() は lock 取得を待つため、
            // _task 代入前に Finish が走って取りこぼす競合を避けられる。
            _task = Task.Run(async () =>
            {
                try
                {
                    await broadcast(ct).ConfigureAwait(false);
                }
                catch
                {
                    // 放送側が自身のエラー/キャンセル後始末を担う前提。ここでは握り潰して必ず Idle へ戻す。
                }
                finally
                {
                    Finish();
                }
            }, CancellationToken.None);
            Transition(State.Broadcasting);
        }
        return true;
    }

    /// <summary>停止（キャンセル要求のみ。静寂化は放送側の後始末が担う）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>停止し、放送タスクの完了（pause 後始末まで）を待つ。終了処理用。</summary>
    public async Task StopAndWaitAsync()
    {
        Task? task;
        lock (_lock)
        {
            _cts?.Cancel();
            task = _task;
        }
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    private void Finish()
    {
        lock (_lock)
        {
            _task = null;
            _cts?.Dispose();
            _cts = null;
            Transition(State.Idle);
        }
    }

    /// <summary>状態遷移とコールバック通知。呼び出しは必ず <c>_lock</c> 保持下で行う。</summary>
    private void Transition(State newState)
    {
        _state = newState;
        _onStateChange?.Invoke(newState);
    }
}
