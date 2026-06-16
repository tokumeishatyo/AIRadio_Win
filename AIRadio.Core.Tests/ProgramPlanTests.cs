using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W13 番組生成（<see cref="ProgramPlan"/>）と番組長（<see cref="ProgramLength"/>）の決定論的契約。
/// 構成: opening → song → 本編（talk, talk, letter, news の繰り返し + 端数）→ ending。
/// </summary>
public class ProgramPlanTests
{
    private static ProgramBlueprint Blueprint() => new(
        Title: "番組",
        AnchorDjId: "zundamon",
        DefaultLength: ProgramLength.FromCorners(10),
        OpeningCritical: true,
        Song: new SongSegmentSpec("spotify:track:X"),
        TalkCornerId: "free_talk",
        LetterCornerId: "letter",
        NewsDjId: "ryusei");

    private static List<(SegmentKind Kind, string? CornerId)> Enumerate(ProgramPlan plan, int max)
    {
        var list = new List<(SegmentKind, string?)>();
        for (var i = 0; i < max; i++)
        {
            if (plan.Segment(i) is not { } seg)
            {
                break;
            }
            list.Add((seg.Kind, seg.CornerId));
        }
        return list;
    }

    private static (SegmentKind, string?) Op => (SegmentKind.Opening, null);
    private static (SegmentKind, string?) Song => (SegmentKind.Song, null);
    private static (SegmentKind, string?) Tf => (SegmentKind.Talk, "free_talk");
    private static (SegmentKind, string?) Tl => (SegmentKind.Talk, "letter");
    private static (SegmentKind, string?) News => (SegmentKind.News, null);
    private static (SegmentKind, string?) Ed => (SegmentKind.Ending, null);

    [Fact]
    public void N1_Produces_OpSongTalkEnding()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(1));

        Assert.Equal(new[] { Op, Song, Tf, Ed }, Enumerate(plan, 20));
        Assert.Null(plan.Segment(4));   // ED の次は null
        Assert.Equal(4, plan.TotalSegmentCount);
    }

    [Fact]
    public void N2_InsertsLetterThenNews_BeforeEnding()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2));

        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Ed }, Enumerate(plan, 20));
        Assert.Equal(7, plan.TotalSegmentCount);
    }

    [Fact]
    public void N3_OddRemainder_TrailingTalkThenEnding_NoLetterNews()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(3));

        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Tf, Ed }, Enumerate(plan, 20));
        Assert.Equal(8, plan.TotalSegmentCount);
    }

    [Fact]
    public void N4_TwoFullPairs_ThenEnding()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(4));

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tf, Tf, Tl, News, Ed }, Enumerate(plan, 30));
        Assert.Equal(11, plan.TotalSegmentCount);
    }

    [Fact]
    public void N10_TotalSegmentCount_Is23()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(10));

        var segments = Enumerate(plan, 100);
        Assert.Equal(23, plan.TotalSegmentCount);
        Assert.Equal(23, segments.Count);                       // ED の次で打ち切り
        Assert.Equal(Ed, segments[^1]);
        Assert.Equal(10, segments.Count(s => s == Tf));          // free_talk は 10 本
        Assert.Equal(5, segments.Count(s => s == Tl));           // letter は 5（ペア 5 組）
        Assert.Equal(5, segments.Count(s => s == News));         // news も 5
    }

    [Fact]
    public void Endless_RepeatsBody_Forever_NoEnding_NullTotal()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.Endless);

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tf, Tf, Tl, News, Tf, Tf }, Enumerate(plan, 12));
        Assert.Null(plan.TotalSegmentCount);
        Assert.NotNull(plan.Segment(1000));                      // 常に非 null
        Assert.DoesNotContain(Ed, Enumerate(plan, 200));         // ED セグメントは無い
    }

    [Fact]
    public void Segment_NegativeIndex_IsNull()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2));
        Assert.Null(plan.Segment(-1));
    }

    [Fact]
    public void OpeningCritical_FromBlueprint_FlowsToSegment()
    {
        var plan = new ProgramPlan(Blueprint() with { OpeningCritical = false }, ProgramLength.FromCorners(1));
        Assert.False(plan.Segment(0)!.Critical);

        var critical = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(1));
        Assert.True(critical.Segment(0)!.Critical);
    }

    [Fact]
    public void Song_And_News_CarryBlueprintFields()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2));
        Assert.Equal("spotify:track:X", plan.Segment(1)!.Song!.FallbackTrackUri);
        Assert.Equal("ryusei", plan.Segment(5)!.DjId);          // news の専任読み手
    }
}

/// <summary>W13 番組長の文字列表現・解析（Mac <c>ProgramLength</c> パリティ）。</summary>
public class ProgramLengthTests
{
    [Fact]
    public void RawValue_Corners_And_Endless()
    {
        Assert.Equal("10", ProgramLength.FromCorners(10).RawValue);
        Assert.Equal("endless", ProgramLength.Endless.RawValue);
    }

    [Theory]
    [InlineData("10", false, 10)]
    [InlineData("0", false, 0)]
    [InlineData("30", false, 30)]
    public void TryParse_ValidCorners(string raw, bool endless, int corners)
    {
        Assert.True(ProgramLength.TryParse(raw, out var length));
        Assert.Equal(endless, length.IsEndless);
        Assert.Equal(corners, length.Corners);
    }

    [Fact]
    public void TryParse_Endless()
    {
        Assert.True(ProgramLength.TryParse("endless", out var length));
        Assert.True(length.IsEndless);
    }

    [Theory]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("10.5")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("10 ")]      // 前後空白は厳格に拒否（Mac Int(rawValue) パリティ）
    [InlineData(" 10")]
    [InlineData("endless ")]
    public void TryParse_Invalid_ReturnsFalse(string? raw)
    {
        Assert.False(ProgramLength.TryParse(raw, out _));
    }

    [Fact]
    public void Equality_IsValueBased_EndlessDistinctFromZeroCorners()
    {
        Assert.Equal(ProgramLength.FromCorners(10), ProgramLength.FromCorners(10));
        Assert.NotEqual(ProgramLength.FromCorners(0), ProgramLength.Endless);
        Assert.NotEqual(ProgramLength.FromCorners(10), ProgramLength.FromCorners(20));
    }
}
