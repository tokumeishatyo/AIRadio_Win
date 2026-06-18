using AIRadio.Infrastructure;
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

        // トレイ常駐は単一インスタンス（2 トレイが Spotify を同時制御する事故を防ぐ。W-Win §2）。
        // CLI デモ（引数あり）は対象外＝複数同時実行を許容。
        using var single = new SingleInstance("AIRadioWinTraySingleInstance");
        if (!single.IsFirstInstance)
        {
            Console.WriteLine("ケイラボAIラジオは既に起動しています（タスクトレイのアイコンをご確認ください）。");
            return 0;
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
