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
/// タスクトレイのアイコン + メニュー（放送を開始⇄停止 / ED で終了 / 番組の長さ▸（10/20/30 本）/ 終了）。
/// Mac 版 `MenuBarController` の移植（W9 + W13）。<see cref="BroadcastSession"/> を保持し、状態変化を
/// <see cref="Dispatcher.UIThread"/> 経由でメニューラベル・活性へ反映する。
/// </summary>
internal sealed class TrayController
{
    /// <summary>メニュー「番組の長さ」の選択肢（仕様 w13 §6-2。エンドレスは UI 非露出）。</summary>
    private static readonly ProgramLength[] LengthChoices =
    {
        ProgramLength.FromCorners(10), ProgramLength.FromCorners(20), ProgramLength.FromCorners(30),
    };

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IRadioLog _log = new ConsoleRadioLog();
    private readonly BroadcastSession _session;
    private readonly BroadcastComposition _broadcast;
    private readonly string _configDir;
    private readonly ProgramLengthStore _lengthStore = new();
    private readonly List<(ProgramLength Choice, NativeMenuItem Item)> _lengthItems = new();

    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _stateItem = new("停止中") { IsEnabled = false };
    private readonly NativeMenuItem _toggleItem = new("放送を開始");
    private readonly NativeMenuItem _endingItem = new("ED で終了") { IsEnabled = false };

    /// <summary>放送中の操作（「ED で終了」）。開始時にセット、Idle で破棄（UI スレッドのみ触る）。</summary>
    private BroadcastControl? _activeControl;

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _configDir = Path.Combine(AppContext.BaseDirectory, "config");
        _broadcast = new BroadcastComposition(_configDir, _log);
        _session = new BroadcastSession(OnStateChanged);

        _toggleItem.Click += (_, _) => Toggle();
        _endingItem.Click += (_, _) => RequestEnding();
        var quitItem = new NativeMenuItem("終了");
        quitItem.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_stateItem);
        menu.Add(_toggleItem);
        menu.Add(_endingItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(BuildLengthMenuItem());
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
            var control = new BroadcastControl();
            _activeControl = control;
            _session.Start(ct => _broadcast.RunAsync(ct, control));
        }
    }

    /// <summary>「ED で終了」: 現セグメント（+ 準備済みの直後トーク）を流したら ED で締める（仕様 w13 §3-4）。</summary>
    private void RequestEnding()
    {
        _activeControl?.RequestEnding();
        _stateItem.Header = "放送中…（ED で終了します）"; // Mac requestEnding() 同様に楽観的更新。
    }

    /// <summary>サブメニュー「番組の長さ」（トーク 10/20/30 本）。選択は %LOCALAPPDATA% 永続化、次の放送開始から反映。</summary>
    private NativeMenuItem BuildLengthMenuItem()
    {
        var lengthMenu = new NativeMenu();
        foreach (var choice in LengthChoices)
        {
            var item = new NativeMenuItem(LengthLabel(choice)) { ToggleType = MenuItemToggleType.Radio };
            var captured = choice;
            item.Click += (_, _) => SelectLength(captured);
            _lengthItems.Add((choice, item));
            lengthMenu.Add(item);
        }
        var root = new NativeMenuItem("番組の長さ") { Menu = lengthMenu };
        RefreshLengthChecks();
        return root;
    }

    private static string LengthLabel(ProgramLength length)
        => length.IsEndless ? "エンドレス" : $"トーク {length.Corners} 本";

    private void SelectLength(ProgramLength choice)
    {
        _lengthStore.Write(choice);
        RefreshLengthChecks();
    }

    /// <summary>現在値（store ?? program.yaml の既定）に応じてラジオチェックを更新。該当なしはどれもチェックしない。</summary>
    private void RefreshLengthChecks()
    {
        var current = _lengthStore.Read() ?? DefaultLength();
        foreach (var (choice, item) in _lengthItems)
        {
            item.IsChecked = choice == current;
        }
    }

    private ProgramLength DefaultLength()
    {
        try { return ProgramConfig.LoadFile(Path.Combine(_configDir, "program.yaml")).DefaultLength; }
        catch { return ProgramLength.FromCorners(10); }
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
                _endingItem.IsEnabled = true;
                break;
            case BroadcastSession.State.Idle:
                _toggleItem.Header = "放送を開始";
                _stateItem.Header = "停止中";
                _endingItem.IsEnabled = false;
                _activeControl = null; // 自走完走・停止のいずれでも確実に破棄（唯一の権威ある状態コールバック）。
                break;
        }
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://AIRadio.App/Assets/tray.ico"));
        return new WindowIcon(stream);
    }
}
