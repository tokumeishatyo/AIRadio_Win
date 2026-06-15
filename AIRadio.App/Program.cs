using Avalonia;

namespace AIRadio.App;

internal static class Program
{
    // 引数なし = タスクトレイ常駐アプリ（W9）。引数あり = CLI デモ（W1-W4 の検証、DemoRunner）。
    // Debug ビルドはコンソールに全ログがストリームする（Mac 版のコンソール起動を踏襲）。
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return DemoRunner.RunAsync(args).GetAwaiter().GetResult();
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia 設定（ビジュアルデザイナからも参照される）。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
