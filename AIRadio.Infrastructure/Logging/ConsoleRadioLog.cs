using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// 進行ログを標準出力へストリームする IRadioLog 実装。
/// Debug ビルド（OutputType=Exe）でコンソールに全ログが流れる（Mac 版のコンソール起動を踏襲）。
/// 秘密値は呼び出し側でマスクしてから渡す（CLAUDE.md §3-3）。
/// </summary>
public sealed class ConsoleRadioLog : IRadioLog
{
    public void Log(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
    }
}
