using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W18 <see cref="YamlJournalStore"/>（<c>journal.local.yaml</c> ⇄ <see cref="StationJournal"/>）。
/// save→load 往復・ファイル無し＝空・壊れ yaml は load で throw（呼び出し側が握り潰す）。Mac <c>JournalStoreTests</c> 移植。
/// </summary>
public class JournalStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"airadio-journal-{Guid.NewGuid():N}.yaml");

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var path = TempPath();
        try
        {
            var store = new YamlJournalStore(path);
            var journal = new StationJournal("2026-W24", new[]
            {
                new JournalEntry("2026-06-14", "ゲストに九州そらさんを迎えました。"),
                new JournalEntry("2026-06-15", "米津玄師さんを特集しました。"),
            });

            store.Save(journal);
            var loaded = store.Load();

            // 値等価は WeekKey + Entries（要素値等価の JournalEntry 列）を個別に検証（whole-object == 非依存）。
            Assert.Equal(journal.WeekKey, loaded.WeekKey);
            Assert.Equal(journal.Entries, loaded.Entries);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public void MissingFile_IsEmptyJournal()
    {
        var loaded = new YamlJournalStore(TempPath()).Load(); // 存在しないパス

        Assert.Equal("", loaded.WeekKey);
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void CorruptYaml_LoadThrows()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not the journal schema");

            // 呼び出し側（BroadcastEngine）が try/catch で握り潰す前提＝store 自体は throw する。
            Assert.ThrowsAny<Exception>(() => new YamlJournalStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public void Save_OverwritesExisting_LeavesNoTempFiles()
    {
        var path = TempPath();
        try
        {
            var store = new YamlJournalStore(path);
            store.Save(new StationJournal("2026-W24", new[] { new JournalEntry("2026-06-14", "一回目") }));
            store.Save(new StationJournal("2026-W24", new[] { new JournalEntry("2026-06-15", "二回目") }));

            var loaded = store.Load();
            Assert.Single(loaded.Entries);
            Assert.Equal("二回目", loaded.Entries[0].Highlight); // 原子的上書きで最新だけが残る

            // 原子的書き込みの temp ファイル（"<path>.<guid>.tmp"）が残っていない。
            var dir = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileName(path);
            Assert.Empty(Directory.GetFiles(dir, stem + ".*.tmp"));
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
