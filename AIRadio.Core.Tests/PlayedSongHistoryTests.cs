using AIRadio.Core;

namespace AIRadio.Core.Tests;

/// <summary>
/// W-DEDUP 演奏済みリング <see cref="PlayedSongHistory"/> の純粋ロジック（TryReserve/IsRecent・FIFO 境界・原子性）。
/// 公開面は ctor / IsRecent / TryReserve のみ（件数 API を足さない＝OBS-7）。上限遵守は IsRecent 経由で観測する。
/// </summary>
public class PlayedSongHistoryTests
{
    private static TrackInfo Track(string uri, string title = "t", string artist = "a")
        => new(uri, title, artist);

    [Fact]
    public void TryReserve_FirstTime_True_ThenIsRecentTrue()
    {
        var h = new PlayedSongHistory(100);
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.True(h.IsRecent("spotify:track:1"));
    }

    [Fact]
    public void TryReserve_SameUriAgain_False()
    {
        var h = new PlayedSongHistory(100);
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.False(h.TryReserve(Track("spotify:track:1")));   // 既出＝予約失敗
    }

    [Fact]
    public void IsRecent_UnknownUri_False()
    {
        var h = new PlayedSongHistory(100);
        h.TryReserve(Track("spotify:track:1"));
        Assert.False(h.IsRecent("spotify:track:2"));
    }

    [Fact]
    public void EmptyUri_TryReserveTrue_NotRecorded()
    {
        var h = new PlayedSongHistory(100);
        Assert.True(h.TryReserve(Track("")));   // 空 URI は採用可（記録しない）
        Assert.False(h.IsRecent(""));
    }

    // --- capacity 境界（off-by-one 固定。N と N+1 を両方アサートし `>` を `>=` 誤実装と区別） ---

    [Fact]
    public void Capacity_ExactlyN_NoEviction()
    {
        const int n = 5;
        var h = new PlayedSongHistory(n);
        for (var i = 0; i < n; i++)
        {
            Assert.True(h.TryReserve(Track($"spotify:track:{i}")));
        }
        for (var i = 0; i < n; i++)
        {
            Assert.True(h.IsRecent($"spotify:track:{i}"));   // N 件は破棄ゼロ（全件残る）
        }
    }

    [Fact]
    public void Capacity_NPlus1_EvictsOldestOnly()
    {
        const int n = 5;
        var h = new PlayedSongHistory(n);
        for (var i = 0; i < n; i++)
        {
            h.TryReserve(Track($"spotify:track:{i}"));
        }
        Assert.True(h.TryReserve(Track($"spotify:track:{n}")));   // N+1 件目
        Assert.False(h.IsRecent("spotify:track:0"));             // 最古 1 件のみ破棄
        for (var i = 1; i <= n; i++)
        {
            Assert.True(h.IsRecent($"spotify:track:{i}"));        // 他は全部残る
        }
    }

    [Fact]
    public void EvictedUri_ReReserved_ReentersAsNewest()
    {
        // 最古が破棄され false になった後、再 TryReserve で true（最新側へ入り直る）。
        // 以後の破棄対象としても最新扱い（eviction の _uris.Remove の正しさを固定）。
        var h = new PlayedSongHistory(3);
        h.TryReserve(Track("spotify:track:0"));
        h.TryReserve(Track("spotify:track:1"));
        h.TryReserve(Track("spotify:track:2"));
        h.TryReserve(Track("spotify:track:3"));   // 0 を破棄。リング=[1,2,3]
        Assert.False(h.IsRecent("spotify:track:0"));

        Assert.True(h.TryReserve(Track("spotify:track:0")));   // 再投入＝最新側。リング=[2,3,0]
        Assert.False(h.IsRecent("spotify:track:1"));            // 1 が押し出された（0 は最新ゆえ残る）
        Assert.True(h.IsRecent("spotify:track:0"));
        Assert.True(h.IsRecent("spotify:track:2"));
        Assert.True(h.IsRecent("spotify:track:3"));
    }

    [Fact]
    public void DuplicateUri_DifferentMeta_IsNoOp_FirstWins()
    {
        // 同一 URI を別 title/artist で再 TryReserve → false（件数増えず・順序も変えない＝first-wins）。
        // 保持メタ自体は公開 API で観測しない（OBS-7）ため、観測可能な「no-op（順序不変）」で代替検証する。
        var h = new PlayedSongHistory(2);
        h.TryReserve(Track("spotify:track:A", "title-A1", "artist-A1"));
        h.TryReserve(Track("spotify:track:B"));
        Assert.False(h.TryReserve(Track("spotify:track:A", "title-A2", "artist-A2")));   // 別メタでも既出＝false

        // no-op なら最古は依然 A。新規 C を入れると最古 A が破棄される（A が最新側へ移っていない＝順序不変）。
        Assert.True(h.TryReserve(Track("spotify:track:C")));
        Assert.False(h.IsRecent("spotify:track:A"));   // 最古のまま破棄（再投入で最新化していない）
        Assert.True(h.IsRecent("spotify:track:B"));
        Assert.True(h.IsRecent("spotify:track:C"));
    }

    [Fact]
    public void Capacity1_SecondImmediatelyEvictsFirst()
    {
        var h = new PlayedSongHistory(1);
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.True(h.TryReserve(Track("spotify:track:2")));   // 1 件目即破棄
        Assert.False(h.IsRecent("spotify:track:1"));
        Assert.True(h.IsRecent("spotify:track:2"));
    }

    [Fact]
    public void Capacity0_Disabled_AlwaysTrue_NeverRecent()
    {
        var h = new PlayedSongHistory(0);
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.True(h.TryReserve(Track("spotify:track:1")));   // 常に true（記録しない）
        Assert.False(h.IsRecent("spotify:track:1"));
    }

    [Fact]
    public void NegativeCapacity_ClampedToZero_Disabled()
    {
        var h = new PlayedSongHistory(-5);   // ctor が 0 にクランプ＝無効
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.True(h.TryReserve(Track("spotify:track:1")));
        Assert.False(h.IsRecent("spotify:track:1"));
    }

    // --- スレッドセーフ / 原子性（CONC-1） ---

    [Fact]
    public void Concurrent_DistinctUris_NoExceptionAndConsistent()
    {
        // 多数スレッドから distinct URI を並行 TryReserve/IsRecent しても例外・デッドロックなく整合（並行バースト）。
        const int count = 1000;
        var h = new PlayedSongHistory(count);
        var allReserved = true;
        Parallel.For(0, count, i =>
        {
            if (!h.TryReserve(Track($"spotify:track:{i}")))
            {
                allReserved = false;   // 競合で失敗したら記録（distinct ゆえ本来すべて成功する）
            }
            _ = h.IsRecent($"spotify:track:{i}");
        });
        Assert.True(allReserved);
        for (var i = 0; i < count; i++)
        {
            Assert.True(h.IsRecent($"spotify:track:{i}"));
        }
    }

    [Fact]
    public void Concurrent_SameUri_ExactlyOneReservationSucceeds()
    {
        // 同一 URI を多数スレッドが同時 TryReserve したとき true はちょうど 1 つ（原子予約）。
        const int threads = 64;
        var h = new PlayedSongHistory(100);
        var successes = 0;
        Parallel.For(0, threads, _ =>
        {
            if (h.TryReserve(Track("spotify:track:contended")))
            {
                Interlocked.Increment(ref successes);
            }
        });
        Assert.Equal(1, successes);
        Assert.True(h.IsRecent("spotify:track:contended"));
    }

    [Fact]
    public void SequentialBeyondCapacity_UpperBoundObservedViaIsRecent()
    {
        // 既知 URI を capacity 個ぶん超えて逐次 TryReserve 後、新しい capacity 個が IsRecent true・
        // 溢れた古い URI が false（上限遵守を IsRecent 経由で観測。件数は公開しない＝§3-5/OBS-7）。
        const int cap = 10;
        const int extra = 5;
        var h = new PlayedSongHistory(cap);
        for (var i = 0; i < cap + extra; i++)
        {
            h.TryReserve(Track($"spotify:track:{i}"));
        }
        for (var i = 0; i < extra; i++)
        {
            Assert.False(h.IsRecent($"spotify:track:{i}"));      // 古い extra 件は押し出された
        }
        for (var i = extra; i < cap + extra; i++)
        {
            Assert.True(h.IsRecent($"spotify:track:{i}"));        // 新しい cap 件は残る
        }
    }
}
