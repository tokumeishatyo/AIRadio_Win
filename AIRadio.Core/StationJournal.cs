using System.Globalization;

namespace AIRadio.Core;

/// <summary>ジャーナル 1 エントリ（1 放送分のハイライト。仕様 w18 §1）。</summary>
/// <param name="Date">放送日（"YYYY-MM-DD"）。</param>
/// <param name="Highlight">その回のハイライト要約文。</param>
public sealed record JournalEntry(string Date, string Highlight);

/// <summary>
/// ステーション・ジャーナル（週次リセットの長期記憶。仕様 w18）。Mac 版 <c>StationJournal</c> の移植。
/// <see cref="WeekKey"/>（ISO 週・月曜始まり）で同一週を判定し、週が替われば過去を忘れる。
/// <para>値等価は <see cref="Entries"/> が <see cref="IReadOnlyList{T}"/>（参照等価）のため whole-object には依存しない。
/// 比較が要るテストは <see cref="WeekKey"/> と <see cref="Entries"/>（要素値等価の <see cref="JournalEntry"/> 列）を個別に検証する。</para>
/// </summary>
public sealed record StationJournal
{
    /// <summary>ISO 週（月曜始まり）のキー "YYYY-Www"（例 "2026-W24"）。週の同一判定に使う。</summary>
    public string WeekKey { get; }

    /// <summary>当週のエントリ（古い順）。</summary>
    public IReadOnlyList<JournalEntry> Entries { get; }

    /// <summary>週内に保持する最大エントリ数（1 日 1 放送想定。超えたら古い順に落とす）。</summary>
    public const int MaxEntries = 7;

    public StationJournal(string weekKey = "", IReadOnlyList<JournalEntry>? entries = null)
    {
        WeekKey = weekKey;
        Entries = entries ?? Array.Empty<JournalEntry>();
    }

    /// <summary>空ジャーナル（weekKey="" / entries=[]）。</summary>
    public static readonly StationJournal Empty = new();

    /// <summary>
    /// ISO 週（月曜始まり）のキー "YYYY-Www"。Mac の <c>Calendar(identifier:.iso8601)</c> の
    /// <c>yearForWeekOfYear</c>/<c>weekOfYear</c> を <see cref="ISOWeek"/> で等価実装する（ISO 8601＝月曜始まり・週番号）。
    /// </summary>
    public static string WeekKeyFor(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local).DateTime;
        return $"{ISOWeek.GetYear(local):D4}-W{ISOWeek.GetWeekOfYear(local):D2}";
    }

    /// <summary>現在週と一致すれば <see cref="Entries"/>、違えば空（週替わりで忘れる。仕様 w18 §5）。</summary>
    public IReadOnlyList<JournalEntry> EntriesForCurrentWeek(DateTimeOffset now, TimeZoneInfo? timeZone = null)
        => WeekKey == WeekKeyFor(now, timeZone) ? Entries : Array.Empty<JournalEntry>();

    /// <summary>
    /// エントリを追記する。週が替わっていれば過去を空にしてから追記し（週次リセットはここで判定）、
    /// <see cref="MaxEntries"/> を超えたら古い順に落とす（リングバッファ）。Mac <c>appended</c> 逐語。
    /// </summary>
    public StationJournal Appended(JournalEntry entry, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var currentWeek = WeekKeyFor(now, timeZone);
        var next = new List<JournalEntry>(WeekKey == currentWeek ? Entries : Array.Empty<JournalEntry>()); // 週替わりでリセット
        next.Add(entry);
        if (next.Count > MaxEntries)
        {
            next.RemoveRange(0, next.Count - MaxEntries);
        }
        return new StationJournal(currentWeek, next);
    }
}

/// <summary>
/// 番組終了時に集める「その回のハイライト」の素材（仕様 w18 §2。確定 A: 日付・ゲスト・特集のみ＝テーマは含めない）。
/// </summary>
/// <param name="Date">放送日（"YYYY-MM-DD"）。</param>
/// <param name="GuestName">ゲスト名（ゲストコーナーが出た回のみ）。</param>
/// <param name="ArtistName">特集アーティスト名（特集が出た回のみ）。</param>
public sealed record BroadcastDigest(string Date, string? GuestName = null, string? ArtistName = null)
{
    /// <summary>振り返る価値のある内容があるか（ゲストも特集も無い回は記録しない）。</summary>
    public bool HasContent => GuestName is not null || ArtistName is not null;
}

/// <summary>
/// ハイライト要約器（LLM で短い記録文に。失敗時は決定論テンプレ。仕様 w18 §2）。
/// <b>完全 fail-tolerant＝throw しない</b>（LLM の例外・空応答はすべて握り潰してフォールバックへ）。Mac <c>JournalSummarizer</c> 移植。
/// </summary>
public sealed class JournalSummarizer
{
    private readonly ILLMBackend _llm;
    private readonly double _temperature;

    /// <param name="temperature">要約の温度（既定 0.6＝global 温度と独立。Mac 一致）。</param>
    public JournalSummarizer(ILLMBackend llm, double temperature = 0.6)
    {
        _llm = llm;
        _temperature = temperature;
    }

    /// <summary>digest を短い記録文 1 文にする。LLM 失敗・空応答なら決定論フォールバック（Mac <c>try?</c> 相当＝全例外を握り潰す）。</summary>
    public async Task<string> SummarizeAsync(BroadcastDigest digest, CancellationToken ct = default)
    {
        var request = new LLMRequest(
            Prompt:
                "次回のラジオ放送の冒頭で「前回はこんな放送でした」と軽く振り返るための、短い記録文を 1 文で書いてください。\n" +
                Facts(digest) +
                "\n- 出力は記録文 1 文のみ。日付・「以上」などの定型句や記号装飾は書かない。長くしない。",
            System: "あなたはラジオ番組の記録係です。次回の振り返りに使う短い一文を書きます。",
            Temperature: _temperature);
        try
        {
            var raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
            var line = raw.Trim(); // Mac .whitespacesAndNewlines＝前後の空白・改行を除去。
            if (line.Length > 0)
            {
                return line;
            }
        }
        catch
        {
            // 長期記憶は事故ゼロ系: 要約失敗（API エラー・空応答・キャンセル等）は握り潰して決定論フォールバックへ。
        }
        return Fallback(digest);
    }

    /// <summary>LLM に渡す素材行（ゲスト・特集。無いものは出さない）。</summary>
    internal static string Facts(BroadcastDigest digest)
    {
        var lines = new List<string>();
        if (digest.GuestName is string guest)
        {
            lines.Add($"ゲスト: {guest}");
        }
        if (digest.ArtistName is string artist)
        {
            lines.Add($"アーティスト特集: {artist}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>決定論フォールバック（LLM 失敗時。素材が無ければ空文字＝記録しない）。</summary>
    internal static string Fallback(BroadcastDigest digest)
    {
        var parts = new List<string>();
        if (digest.GuestName is string guest)
        {
            parts.Add($"ゲストに{guest}さんを迎え");
        }
        if (digest.ArtistName is string artist)
        {
            parts.Add($"{artist}さんを特集し");
        }
        return parts.Count == 0 ? "" : string.Join("、", parts) + "ました。";
    }
}
