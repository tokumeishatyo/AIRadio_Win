using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using AIRadio.App.Composition;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.App;

/// <summary>
/// タスクトレイのアイコン + メニュー（開始 / 停止 / 終了）。Mac 版 `MenuBarController` の移植。
/// <see cref="BroadcastSession"/> を保持し、状態変化を <see cref="Dispatcher.UIThread"/> 経由でメニューラベルへ反映する。
/// </summary>
internal sealed class TrayController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IRadioLog _log = new ConsoleRadioLog();
    private readonly BroadcastSession _session;
    private readonly BroadcastComposition _broadcast;

    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _stateItem = new("停止中") { IsEnabled = false };
    private readonly NativeMenuItem _toggleItem = new("放送を開始");

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        _broadcast = new BroadcastComposition(configDir, _log);
        _session = new BroadcastSession(OnStateChanged);

        _toggleItem.Click += (_, _) => Toggle();
        var quitItem = new NativeMenuItem("終了");
        quitItem.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_stateItem);
        menu.Add(_toggleItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "ケイラボAIラジオ",
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

        _log.Log("ケイラボAIラジオ — タスクトレイに常駐します（アイコンのメニューから開始 / 停止）。");
    }

    private void Toggle()
    {
        if (_session.CurrentState == BroadcastSession.State.Broadcasting)
        {
            _session.Stop();
        }
        else
        {
            _session.Start(ct => _broadcast.RunAsync(ct));
        }
    }

    private void Quit() => _ = ShutdownAsync();

    /// <summary>graceful shutdown: 放送停止 → 後始末 pause 完了待ち → 終了（鳴らしっぱなし防止 §3-1）。</summary>
    private async Task ShutdownAsync()
    {
        await _session.StopAndWaitAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _trayIcon.IsVisible = false;
            _desktop.Shutdown(0);
        });
    }

    // BroadcastSession の lock 内から呼ばれるため、UI スレッドへ非ブロッキングにマーシャルする。
    private void OnStateChanged(BroadcastSession.State state)
        => Dispatcher.UIThread.Post(() => Apply(state));

    private void Apply(BroadcastSession.State state)
    {
        switch (state)
        {
            case BroadcastSession.State.Broadcasting:
                _toggleItem.Header = "放送を停止";
                _stateItem.Header = "放送中…";
                break;
            case BroadcastSession.State.Idle:
                _toggleItem.Header = "放送を開始";
                _stateItem.Header = "停止中";
                break;
        }
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://AIRadio.App/Assets/tray.ico"));
        return new WindowIcon(stream);
    }
}
