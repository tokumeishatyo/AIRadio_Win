using System.Text;
using System.Text.RegularExpressions;

namespace AIRadio.Core;

/// <summary>選曲の依頼内容（コーナーの締め曲・番組の冒頭曲などで共用）。</summary>
public sealed record SongRequest(string Context, string FallbackTrackUri, string PromptHint = "");

/// <summary>
/// LLM に候補曲を挙げさせ、プレフライト（検索 + 再生可否）で最初に再生可能な曲を確定する（CLAUDE.md §3-2）。
/// 全滅・検索不調の場合は <see cref="SongRequest.FallbackTrackUri"/> に倒す（曲名不明のまま返す）。Mac 版 `SongPicker` の移植。
/// 実差し替え境界ではない（design §2 に `SongPicking` を含めない）ため具象クラス（§3-5）。
/// </summary>
public sealed class SongPicker
{
    public const int MaxCandidates = 5;

    private readonly ILLMBackend _llm;
    private readonly ITrackSearcher _searcher;
    private readonly double _temperature;

    public SongPicker(ILLMBackend llm, ITrackSearcher searcher, double temperature = 0.9)
    {
        _llm = llm;
        _searcher = searcher;
        _temperature = temperature;
    }

    public async Task<TrackInfo> PickAsync(SongRequest request, CancellationToken ct = default)
    {
        var raw = await _llm.GenerateAsync(MakeRequest(request, _temperature), ct).ConfigureAwait(false);
        foreach (var (title, artist) in ParseCandidates(raw).Take(MaxCandidates))
        {
            // 候補単位の検索失敗は握り潰して次の候補へ（fail-tolerant）。キャンセルは伝播。
            IReadOnlyList<TrackInfo> results;
            try
            {
                results = await _searcher.SearchAsync($"{title} {artist}", 3, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }
            var track = results.FirstOrDefault(t => t.IsPlayable);
            if (track is not null)
            {
                return track;
            }
        }
        return new TrackInfo(request.FallbackTrackUri, "", "");
    }

    // MARK: - プロンプト構築

    public static LLMRequest MakeRequest(SongRequest request, double temperature = 0.9)
    {
        var prompt = new StringBuilder()
            .Append(request.Context).Append("の候補を ").Append(MaxCandidates).Append(" 曲挙げてください。\n\n")
            .Append("# 制約\n")
            .Append("- 出力は 1 行につき 1 曲、「曲名 - アーティスト名」の形式のみ。番号・説明・装飾は書かない。\n")
            .Append("- 実在する曲だけを挙げる（Spotify で配信されている可能性が高いもの）。");
        if (request.PromptHint.Length > 0)
        {
            prompt.Append("\n- 選曲のヒント: ").Append(request.PromptHint);
        }
        return new LLMRequest(prompt.ToString(), Temperature: temperature);
    }

    // MARK: - パース

    // 行頭の箇条書き記号・番号（"- " "* " "1. " "2) " など）。
    private static readonly Regex BulletPrefix = new(@"^(?:[-*•]\s+|\d+[.)]\s*)+", RegexOptions.Compiled);

    /// <summary>`曲名 - アーティスト名` 形式の行を抽出する。番号・箇条書きの前置きは許容、形式外の行は捨てる。</summary>
    public static IReadOnlyList<(string Title, string Artist)> ParseCandidates(string raw)
    {
        var result = new List<(string, string)>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = BulletPrefix.Replace(Whitespace.TrimHorizontal(rawLine), "");
            var index = line.IndexOf(" - ", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }
            var title = Whitespace.TrimHorizontal(line[..index]);
            var artist = Whitespace.TrimHorizontal(line[(index + 3)..]);
            if (title.Length > 0 && artist.Length > 0)
            {
                result.Add((title, artist));
            }
        }
        return result;
    }
}
