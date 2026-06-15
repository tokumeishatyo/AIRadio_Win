using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AIRadio.App;

/// <summary>
/// Avalonia アプリ本体。**専用ウィンドウを持たないトレイ常駐**（CLAUDE.md §2 / design §2）。
/// <see cref="ShutdownMode.OnExplicitShutdown"/> でウィンドウ無しでも常駐し、終了は <see cref="TrayController"/> から明示的に行う。
/// </summary>
public partial class App : Application
{
    private TrayController? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // ウィンドウ無しで常駐
            _tray = new TrayController(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
