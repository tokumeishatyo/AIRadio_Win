using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-Win 単一インスタンス（名前付き Mutex の存在検出）。</summary>
public class SingleInstanceTests
{
    private static string UniqueName() => $"AIRadioTest-{Guid.NewGuid():N}";

    [Fact]
    public void First_IsFirst_SecondSameName_IsNot()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);   // 1 個目を生かしたまま 2 個目

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void DifferentNames_BothFirst()
    {
        using var a = new SingleInstance(UniqueName());
        using var b = new SingleInstance(UniqueName());

        Assert.True(a.IsFirstInstance);
        Assert.True(b.IsFirstInstance);
    }

    [Fact]
    public void AfterDispose_SameName_IsFirstAgain()
    {
        var name = UniqueName();
        var first = new SingleInstance(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose();   // ハンドルが閉じて名前が破棄される

        using var reacquired = new SingleInstance(name);
        Assert.True(reacquired.IsFirstInstance);
    }
}
