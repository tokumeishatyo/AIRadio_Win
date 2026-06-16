using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W13 番組長の永続化（%LOCALAPPDATA% 平文ファイル）。一時パスへ隔離してファイル IO を検証する。</summary>
public class ProgramLengthStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"airadio-test-{Guid.NewGuid():N}", "program-length");

    [Fact]
    public void Write_Then_Read_RoundTrips_Corners()
    {
        var path = TempPath();
        try
        {
            var store = new ProgramLengthStore(path);
            store.Write(ProgramLength.FromCorners(20));

            Assert.Equal(ProgramLength.FromCorners(20), new ProgramLengthStore(path).Read());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Write_Then_Read_RoundTrips_Endless()
    {
        var path = TempPath();
        try
        {
            new ProgramLengthStore(path).Write(ProgramLength.Endless);
            var read = new ProgramLengthStore(path).Read();
            Assert.NotNull(read);
            Assert.True(read!.Value.IsEndless);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Read_Absent_ReturnsNull()
    {
        var path = TempPath(); // 作成しない
        Assert.Null(new ProgramLengthStore(path).Read());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]        // 空ファイル → Trim 後 "" → null（再起動後は既定へ倒す）
    [InlineData("   ")]     // 空白のみ → Trim 後 "" → null
    [InlineData("-3")]      // 負数 → null
    public void Read_InvalidOrEmptyContent_ReturnsNull(string content)
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Assert.Null(new ProgramLengthStore(path).Read());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Read_TrimsWhitespace()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "  30\n");
            Assert.Equal(ProgramLength.FromCorners(30), new ProgramLengthStore(path).Read());
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
        catch { /* テスト後始末のベストエフォート */ }
    }
}
