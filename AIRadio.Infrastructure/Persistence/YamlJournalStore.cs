using System.Text;
using AIRadio.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/journal.local.yaml</c> への <see cref="StationJournal"/> 永続化（長期記憶。仕様 w18 §3）。Mac <c>YamlJournalStore</c> 移植。
/// ファイル無し＝空ジャーナル。壊れていたら <see cref="Load"/> が throw するが、呼び出し側（<c>BroadcastEngine</c>）が
/// 握り潰して空に倒す（長期記憶は事故ゼロ系＝壊れても放送を止めない）。人為削除で即クリア。
/// 書き込みは原子的（一意 temp → <see cref="File.Replace"/>（既存時）/ <see cref="File.Move(string,string)"/>（初回）・失敗時は
/// temp を best-effort 削除・BOM なし UTF-8）。<c>ArtistListGenerator.Write</c> と同じ確立済み idiom（Mac <c>atomically:true</c> 移植）。
/// </summary>
public sealed class YamlJournalStore : IJournalStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly string _path;

    public YamlJournalStore(string path) => _path = path;

    public StationJournal Load()
    {
        if (!File.Exists(_path))
        {
            return StationJournal.Empty;
        }
        var yaml = File.ReadAllText(_path);
        var dto = YamlConfigLoader.Deserialize<FileDto>(yaml)
            ?? throw ConfigException.MissingField("journal"); // 空ファイル等（caller が握り潰す）。
        var entries = (dto.Entries ?? new List<EntryDto>())
            .Select(e => new JournalEntry(e.Date ?? "", e.Highlight ?? ""))
            .ToList();
        return new StationJournal(dto.WeekKey ?? "", entries);
    }

    public void Save(StationJournal journal)
    {
        var dto = new FileDto
        {
            WeekKey = journal.WeekKey,
            Entries = journal.Entries.Select(e => new EntryDto { Date = e.Date, Highlight = e.Highlight }).ToList(),
        };
        var yaml = Serializer.Serialize(dto);

        // 原子的上書き（ArtistListGenerator.Write と同 idiom）: 一意 temp に BOM なしで書いてから
        // 既存は File.Replace・初回は File.Move。失敗時は temp を best-effort 削除して rethrow（config/ に temp を残さない）。
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".";
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort: 一時ファイル掃除 */ }
            throw;
        }
    }

    private sealed class FileDto
    {
        public string? WeekKey { get; set; }
        public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Date { get; set; }
        public string? Highlight { get; set; }
    }
}
