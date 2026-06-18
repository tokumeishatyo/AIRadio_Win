using System.Text;
using System.Text.RegularExpressions;
using AIRadio.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AIRadio.Infrastructure;

/// <summary>
/// アーティスト一覧の生成（メニュー「アーティスト一覧を生成」の共通処理。仕様 w15 §9-3）。
/// <c>artist-gen.yaml</c> の <c>genre_prompt</c> のジャンルで LLM に挙げさせ、<see cref="IArtistCatalog"/> で実在検証し、
/// <c>config/artists.yaml</c> を原子的に上書きする（オフライン・音は出さない）。単一消費者ゆえ具象クラス（interface なし、§3-5）。
/// reading（全角カタカナ読み・VOICEVOX 辞書同期用）も LLM に併出させて保存する（W19b。読み不明なら名前のみ）。
/// </summary>
public sealed class ArtistListGenerator
{
    private readonly ILLMBackend _llm;
    private readonly IArtistCatalog _catalog;
    private readonly double _temperature;

    public ArtistListGenerator(ILLMBackend llm, IArtistCatalog catalog, double temperature = 1.0)
    {
        _llm = llm;
        _catalog = catalog;
        _temperature = temperature;
    }

    /// <summary>
    /// 生成して <paramref name="path"/>（<c>config/artists.yaml</c>）へ原子的に上書きする。返り値は確定組数。
    /// 実在検証で 0 組なら <see cref="ArtistGenException"/> を throw し、ファイルは変更しない。
    /// </summary>
    public async Task<int> GenerateAsync(
        ArtistGenConfig config, string path, int maxRetries = 2, CancellationToken ct = default)
    {
        var seen = new HashSet<string>();                          // 正規化名で去重
        var entries = new List<(string Name, string? Reading)>();  // 採用順（reading 付き。W19b）
        var attempt = 0;
        while (entries.Count < config.TargetCount && attempt <= maxRetries)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();
            var want = config.TargetCount - entries.Count;
            var request = MakeRequest(config, want, entries.Select(e => e.Name).ToList(), _temperature);
            string raw;
            try
            {
                raw = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                raw = "";
            }

            var addedThisRound = 0;
            foreach (var candidate in ParseEntries(raw))
            {
                if (entries.Count >= config.TargetCount)
                {
                    break;
                }
                var canonical = ArtistsConfig.CanonicalName(candidate.Name);
                if (canonical.Length == 0 || !seen.Add(canonical))
                {
                    continue;
                }
                // 実在検証: Spotify でその名前の曲が見つかるか（非実在・配信なし・ジャンル外を間引く）。
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<TrackInfo> tracks;
                try
                {
                    tracks = await _catalog.TopTracksAsync(candidate.Name, 1, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    tracks = Array.Empty<TrackInfo>();
                }
                if (tracks.Count == 0)
                {
                    continue;
                }
                entries.Add(candidate);
                addedThisRound++;
            }
            if (addedThisRound == 0)
            {
                break;   // これ以上増えない（LLM 枯渇）なら打ち切り。
            }
        }
        if (entries.Count == 0)
        {
            throw new ArtistGenException();
        }

        var artists = entries
            .Select((e, i) => new ArtistProfile($"artist_{i + 1:D3}", e.Name, e.Reading))
            .ToList();
        Write(artists, path);
        return artists.Count;
    }

    // プロンプト / パース / 書き出し ----------------------------------------

    /// <summary>1 行 1 組＝「アーティスト名 TAB 全角カタカナ読み」を要求（W19b。読み不明なら名前のみ可）。</summary>
    public static LLMRequest MakeRequest(ArtistGenConfig config, int want, IReadOnlyList<string> excluding, double temperature)
    {
        var prompt = new StringBuilder();
        prompt.Append(config.GenrePrompt).Append('\n');
        prompt.Append($"上記に該当する音楽アーティスト（バンド・ソロ・グループ）を {Math.Max(want, 1)} 組、できるだけ多く挙げてください。\n\n");
        prompt.Append("# 出力形式\n");
        prompt.Append("- 1 行につき 1 組。「アーティスト名」と「全角カタカナの読み」をタブ区切りで書く（例: 米津玄師\tヨネヅケンシ）。\n");
        prompt.Append("- 読みが分からなければ名前だけでもよい（タブと読みを省略）。\n");
        prompt.Append("- 番号・説明・装飾・補足は書かない。\n\n");
        prompt.Append("# 制約\n");
        prompt.Append("- 実在し、Spotify で配信されている可能性が高い有名どころを中心に。\n");
        prompt.Append("- 重複・別表記の重複を避ける。");
        if (excluding.Count > 0)
        {
            // 既出を除く（リトライ時に重複を減らす）。長すぎないよう直近分のみ。
            var recent = string.Join("、", excluding.TakeLast(80));
            prompt.Append($"\n- 次のアーティストは既出なので除く: {recent}");
        }
        return new LLMRequest(prompt.ToString(), Temperature: temperature);
    }

    private static readonly char[] EntryTrimChars = { '"', '\'', '「', '」', ' ', '　', '\r' };

    private static readonly Regex BulletPrefix = new(@"^(?:[-*•]\s+|\d+[.)]\s*)+", RegexOptions.Compiled);

    /// <summary>
    /// 1 行を「名前&lt;TAB&gt;カタカナ読み」として解釈する（W19b §3.3）。最初のタブで 2 分割（3 列目以降無視）。
    /// 名前は行全体 <see cref="string.Trim()"/>（<c>\r</c> 含む）＋ 行頭の箇条書き・番号・引用符を除去、空名はスキップ。
    /// 読みは引用符・空白・<c>\r</c> を除去 → NFKC 正規化 → カタカナ検証し、非カタカナ・空なら null（読み無しで救済）。
    /// </summary>
    public static IReadOnlyList<(string Name, string? Reading)> ParseEntries(string raw)
    {
        var result = new List<(string Name, string? Reading)>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var columns = rawLine.Split('\t');
            var name = columns[0].Trim();
            name = BulletPrefix.Replace(name, "");
            name = name.Trim(EntryTrimChars);
            if (name.Length == 0)
            {
                continue;
            }

            string? reading = null;
            if (columns.Length > 1)
            {
                var candidate = VoicevoxUserDict.NormalizePronunciation(columns[1].Trim(EntryTrimChars));
                if (VoicevoxUserDict.IsKatakana(candidate))
                {
                    reading = candidate;
                }
            }
            result.Add((name, reading));
        }
        return result;
    }

    /// <summary>
    /// YAML として原子的に書き出す（一意な一時ファイル → <see cref="File.Replace"/>（既存時）/ <see cref="File.Move"/>（初回）。
    /// 途中クラッシュで <c>artists.yaml</c> を壊さない。UTF-8 BOM なし＝§18-15）。
    /// </summary>
    public static void Write(IReadOnlyList<ArtistProfile> artists, string path)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)   // reading=null は `reading:` を出力しない（W19b）。
            .Build();
        var body = serializer.Serialize(new Out
        {
            Artists = artists.Select(a => new OutArtist { Id = a.Id, Name = a.Name, Reading = a.Reading }).ToList(),
        });
        var header =
            "# アーティスト特集（仕様 w15）のプール。メニュー「アーティスト一覧を生成」が生成・上書きする。\n"
            + "# 手編集可（id は一意・name は必須・reading は任意のカタカナ読み 仕様 w19b）。ジャンル・件数は config/artist-gen.yaml で指定。\n";
        var content = header + body;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"artists.yaml.{Guid.NewGuid():N}.tmp");
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(temp, content, utf8NoBom);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort: 一時ファイル掃除 */ }
            throw;
        }
    }

    private sealed class Out
    {
        public List<OutArtist> Artists { get; set; } = new();
    }

    private sealed class OutArtist
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Reading { get; set; }   // null は OmitNull で出力されない（W19b）。
    }
}

/// <summary>アーティスト生成（オフライン）のエラー。放送系の <see cref="RadioException"/> 体系には載せない（生成ツール固有）。</summary>
public sealed class ArtistGenException : Exception
{
    public ArtistGenException()
        : base("アーティストを生成できませんでした（LLM 応答が空、または実在検証で全滅）。")
    {
    }
}
