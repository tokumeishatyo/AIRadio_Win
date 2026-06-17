using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W13.5 <see cref="ThemedSegment.Spiel"/> のフォールバック連鎖（メイン優先 → fallbacks の順 → 任意 1 件 → 空で null）。
/// BroadcastEngine は <c>Spiel(main.Id, [anchor.Id]) ?? new DjSpiel("")</c> でこの連鎖に依存する。
/// </summary>
public class ThemedSegmentTests
{
    private static ThemeConfig Staging => new(null, "spotify:track:x", 5, 100, 35, 10);

    private static ThemedSegment Segment(params string[] ids)
        => new(Staging, ids.ToDictionary(id => id, id => new DjSpiel($"{id}口上")));

    [Fact]
    public void Spiel_PrefersMain_WhenPresent()
    {
        var seg = Segment("zundamon", "metan");
        Assert.Equal("metan口上", seg.Spiel("metan", new[] { "zundamon" })!.Announcement);
    }

    [Fact]
    public void Spiel_FallsBackToFallbacksInOrder_WhenMainAbsent()
    {
        var seg = Segment("zundamon"); // metan は無い → anchor(zundamon) へ
        Assert.Equal("zundamon口上", seg.Spiel("metan", new[] { "zundamon" })!.Announcement);
    }

    [Fact]
    public void Spiel_FallsBackToAnyOne_WhenNeitherPresent()
    {
        var seg = Segment("tsumugi"); // main も fallback も無い → 任意の 1 件
        Assert.Equal("tsumugi口上", seg.Spiel("metan", new[] { "zundamon" })!.Announcement);
    }

    [Fact]
    public void Spiel_ReturnsNull_WhenByDjEmpty()
    {
        var seg = new ThemedSegment(Staging, new Dictionary<string, DjSpiel>());
        Assert.Null(seg.Spiel("metan", new[] { "zundamon" })); // 呼び出し側は空口上にフォールバック
    }
}
