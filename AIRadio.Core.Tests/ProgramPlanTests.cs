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
    private static (SegmentKind, string?) Tg => (SegmentKind.Talk, "guest");

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

    // --- W14: ゲスト挿入（最初の news 直後に 1 回） ---

    private static ProgramBlueprint GuestBlueprint() => Blueprint() with { GuestCornerId = "guest" };

    [Fact]
    public void Guest_N2_InsertedAfterFirstNews()
    {
        var plan = new ProgramPlan(GuestBlueprint(), ProgramLength.FromCorners(2));

        Assert.True(plan.IncludesGuestCorner);
        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Tg, Ed }, Enumerate(plan, 20));
        Assert.Equal(8, plan.TotalSegmentCount);
    }

    [Fact]
    public void Guest_N4_OnlyAfterFirstNews_NotSecond()
    {
        var plan = new ProgramPlan(GuestBlueprint(), ProgramLength.FromCorners(4));

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tf, Tf, Tl, News, Ed }, Enumerate(plan, 30));
        Assert.Equal(12, plan.TotalSegmentCount);
        Assert.Equal(1, Enumerate(plan, 30).Count(s => s == Tg)); // ゲストは 1 回だけ（2 回目 news 後はなし）
        Assert.Equal(4, Enumerate(plan, 30).Count(s => s == Tf)); // free_talk は N=4 のまま（ゲストは N に数えない）
    }

    [Fact]
    public void Guest_N1_NoGuest_BelowMinimum()
    {
        var plan = new ProgramPlan(GuestBlueprint(), ProgramLength.FromCorners(1));

        Assert.False(plan.IncludesGuestCorner);
        Assert.Equal(new[] { Op, Song, Tf, Ed }, Enumerate(plan, 20)); // news が無いのでゲストもなし
        Assert.Equal(4, plan.TotalSegmentCount);
    }

    [Fact]
    public void Guest_Endless_InsertedOnce()
    {
        var plan = new ProgramPlan(GuestBlueprint(), ProgramLength.Endless);

        Assert.True(plan.IncludesGuestCorner);
        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tf, Tf, Tl, News, Tf }, Enumerate(plan, 12));
        Assert.Equal(1, Enumerate(plan, 200).Count(s => s == Tg)); // 無限ループでもゲストは 1 回
    }

    [Fact]
    public void Guest_NullCornerId_PlanUnchanged()
    {
        var plan = new ProgramPlan(Blueprint(), ProgramLength.FromCorners(2)); // GuestCornerId なし

        Assert.False(plan.IncludesGuestCorner);
        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Ed }, Enumerate(plan, 20)); // W13 の N=2 と同じ
        Assert.Equal(7, plan.TotalSegmentCount);
    }

    // --- W15: アーティスト特集挿入（ゲスト直後 = body 5 に 1 回。特集はゲストに従属） ---

    private static (SegmentKind, string?) Tfeat => (SegmentKind.ArtistFeature, "artist_feature");

    private static ProgramBlueprint FeatureBlueprint() =>
        Blueprint() with { GuestCornerId = "guest", ArtistFeatureCornerId = "artist_feature" };

    private static ProgramBlueprint FeatureOnlyBlueprint() =>
        Blueprint() with { ArtistFeatureCornerId = "artist_feature" }; // guest 無効

    [Fact]
    public void Feature_N2_AfterGuest_TotalPlus2()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.FromCorners(2));

        Assert.True(plan.IncludesArtistFeature);
        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tfeat, Ed }, Enumerate(plan, 20));
        Assert.Equal(9, plan.TotalSegmentCount);            // ゲスト/特集なし 7 → +2
        Assert.Equal(SegmentKind.ArtistFeature, plan.Segment(7)!.Kind);
        Assert.Equal("artist_feature", plan.Segment(7)!.CornerId);
        Assert.False(plan.Segment(7)!.Critical);            // 特集は常に非 critical
    }

    [Fact]
    public void Feature_N3_Odd_TrailingTalkShiftedByTwoInsertions()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.FromCorners(3));

        Assert.Equal(new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tfeat, Tf, Ed }, Enumerate(plan, 20));
        Assert.Equal(10, plan.TotalSegmentCount);           // 8 → +2
    }

    [Fact]
    public void Feature_N4_OnlyAfterFirstNews_NotSecond()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.FromCorners(4));

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tfeat, Tf, Tf, Tl, News, Ed }, Enumerate(plan, 30));
        Assert.Equal(1, Enumerate(plan, 30).Count(s => s == Tfeat)); // 特集は 1 回だけ
        Assert.Equal(4, Enumerate(plan, 30).Count(s => s == Tf));    // free_talk は N=4 のまま（特集は N に数えない）
        Assert.Equal(13, plan.TotalSegmentCount);           // 11 → +2
    }

    [Fact]
    public void Feature_N5_TrailingTalkShifted()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.FromCorners(5));

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tfeat, Tf, Tf, Tl, News, Tf, Ed }, Enumerate(plan, 30));
    }

    [Fact]
    public void Feature_Endless_InsertedOnce_NullTotal()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.Endless);

        Assert.Equal(
            new[] { Op, Song, Tf, Tf, Tl, News, Tg, Tfeat }, Enumerate(plan, 8));
        Assert.Equal(1, Enumerate(plan, 200).Count(s => s == Tfeat)); // 無限ループでも特集は 1 回
        Assert.Null(plan.TotalSegmentCount);
    }

    [Fact]
    public void Feature_RequiresGuest_NoneWhenGuestDisabled()
    {
        var plan = new ProgramPlan(FeatureOnlyBlueprint(), ProgramLength.FromCorners(4));

        Assert.False(plan.IncludesArtistFeature);                       // ゲスト無効＝特集も従属して無効
        Assert.DoesNotContain(Tfeat, Enumerate(plan, 30));
    }

    [Fact]
    public void Feature_N1_NoNews_NoGuestNoFeature()
    {
        var plan = new ProgramPlan(FeatureBlueprint(), ProgramLength.FromCorners(1));

        Assert.Equal(new[] { Op, Song, Tf, Ed }, Enumerate(plan, 20)); // news が無いのでゲストも特集もなし
        Assert.Equal(4, plan.TotalSegmentCount);
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
