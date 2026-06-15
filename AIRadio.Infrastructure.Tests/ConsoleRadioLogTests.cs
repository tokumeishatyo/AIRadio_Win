using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class ConsoleRadioLogTests
{
    [Fact]
    public void Log_WritesMessageToStandardOutput()
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            new ConsoleRadioLog().Log("テスト進行ログ");
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("テスト進行ログ", captured.ToString());
    }
}
