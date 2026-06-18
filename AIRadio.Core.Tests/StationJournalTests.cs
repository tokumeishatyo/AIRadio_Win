using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

/// <summary>
/// W18 ステーション・ジャーナル（週次長期記憶）のモデル契約。ISO 週（月曜始まり）の週キー・週リセット・
/// リングバッファ・要約器（LLM/フォールバック）。Mac <c>StationJournalTests</c>/<c>JournalSummarizerTests</c> 移植。
/// 週キーは tz 非依存にするため UTC 固定（ISO 週は壁時計の日付で決まる）。
/// </summary>
public class StationJournalTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int hour = 12)
        => new(y, m, d, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WeekKey_IsoMondayStart_SatSunSameWeek_NextMondayRolls()
    {
        // 2026-06-13 土 / 06-14 日 = 同一 ISO 週。06-15 月 = 翌週（ISO 週は月曜始まり）。
        var sat = StationJournal.WeekKeyFor(Utc(2026, 6, 13), TimeZoneInfo.Utc);
        var sun = StationJournal.WeekKeyFor(Utc(2026, 6, 14), TimeZoneInfo.Utc);
        var mon = StationJournal.WeekKeyFor(Utc(2026, 6, 15), TimeZoneInfo.Utc);

        Assert.Equal(sat, sun);     // 同じ ISO 週
        Assert.NotEqual(sun, mon);  // 月曜で週替わり
        Assert.Matches(@"^\d{4}-W\d{2}$", mon); // "YYYY-Www" 形式
    }

    [Fact]
    public void WeekKey_RespectsTimeZone_AcrossWeekBoundary()
    {
        // 同一インスタント（日曜 23:30 UTC）が、UTC では当週・+9 tz では翌月曜＝翌週キー（ConvertTime の tz 解釈）。
        var instant = new DateTimeOffset(2026, 6, 14, 23, 30, 0, TimeSpan.Zero); // 日 23:30 UTC = 月 08:30 (+9)
        var jst = TimeZoneInfo.CreateCustomTimeZone("JST9", TimeSpan.FromHours(9), "JST9", "JST9");

        Assert.NotEqual(
            StationJournal.WeekKeyFor(instant, TimeZoneInfo.Utc),
            StationJournal.WeekKeyFor(instant, jst));
    }

    [Fact]
    public void EntriesForCurrentWeek_MatchReturnsEntries_MismatchReturnsEmpty()
    {
        var key = StationJournal.WeekKeyFor(Utc(2026, 6, 14), TimeZoneInfo.Utc);
        var journal = new StationJournal(key, new[] { new JournalEntry("2026-06-14", "h") });

        Assert.Single(journal.EntriesForCurrentWeek(Utc(2026, 6, 14), TimeZoneInfo.Utc));
        Assert.Empty(journal.EntriesForCurrentWeek(Utc(2026, 6, 15), TimeZoneInfo.Utc)); // 翌週は空
    }

    [Fact]
    public void Appended_ResetsOnNewWeek_DropsPreviousWeek()
    {
        var oldKey = StationJournal.WeekKeyFor(Utc(2026, 6, 14), TimeZoneInfo.Utc); // 先週（日）
        var journal = new StationJournal(oldKey, new[] { new JournalEntry("2026-06-14", "先週") });

        var updated = journal.Appended(new JournalEntry("2026-06-15", "今週"), Utc(2026, 6, 15), TimeZoneInfo.Utc);

        Assert.Equal(new[] { "今週" }, updated.Entries.Select(e => e.Highlight)); // 先週分は消える
        Assert.Equal(StationJournal.WeekKeyFor(Utc(2026, 6, 15), TimeZoneInfo.Utc), updated.WeekKey);
    }

    [Fact]
    public void Appended_SameWeek_AppendsAndRingBuffersAtMaxEntries()
    {
        var key = StationJournal.WeekKeyFor(Utc(2026, 6, 15), TimeZoneInfo.Utc);
        var journal = new StationJournal(
            key, Enumerable.Range(1, StationJournal.MaxEntries).Select(i => new JournalEntry($"d{i}", $"h{i}")).ToList());

        journal = journal.Appended(new JournalEntry("d8", "h8"), Utc(2026, 6, 15), TimeZoneInfo.Utc);

        Assert.Equal(StationJournal.MaxEntries, journal.Entries.Count); // 7
        Assert.Equal("h2", journal.Entries[0].Highlight);              // h1 が落ちる
        Assert.Equal("h8", journal.Entries[^1].Highlight);
    }
}

/// <summary>W18 ハイライト要約器（LLM 要約・決定論フォールバック）。Mac <c>JournalSummarizerTests</c> 移植。</summary>
public class JournalSummarizerTests
{
    [Fact]
    public async Task UsesLlmResponse_TrimsWhitespace()
    {
        var summarizer = new JournalSummarizer(new ScriptedLLM("  ゲストに九州そらさんを迎えました。 "));

        var text = await summarizer.SummarizeAsync(new BroadcastDigest("2026-06-14", GuestName: "九州そら"));

        Assert.Equal("ゲストに九州そらさんを迎えました。", text);
    }

    [Fact]
    public async Task FallsBackOnLlmFailure_PutsGuestAndArtistInSentence()
    {
        // 空 ScriptedLLM は EmptyResponse を投げる＝LLM 失敗。要約器は握り潰して決定論フォールバックへ。
        var summarizer = new JournalSummarizer(new ScriptedLLM());

        var text = await summarizer.SummarizeAsync(
            new BroadcastDigest("2026-06-14", GuestName: "あんこもん", ArtistName: "米津玄師"));

        Assert.Contains("あんこもん", text);
        Assert.Contains("米津玄師", text);
    }

    [Fact]
    public async Task EmptyWhenNoContent_AndHasContentReflectsMaterial()
    {
        var text = await new JournalSummarizer(new ScriptedLLM()).SummarizeAsync(new BroadcastDigest("x"));

        Assert.Equal("", text);
        Assert.False(new BroadcastDigest("x").HasContent);
        Assert.True(new BroadcastDigest("x", GuestName: "g").HasContent);
        Assert.True(new BroadcastDigest("x", ArtistName: "a").HasContent);
    }
}
