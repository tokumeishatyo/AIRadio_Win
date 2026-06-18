using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>読み辞書同期の結果サマリ（呼び出し側がログに出す。仕様 W19a §5）。</summary>
public record struct PronunciationSyncSummary(
    int Added = 0,
    int Updated = 0,
    int Skipped = 0,
    int Failed = 0,
    bool Unreachable = false);

/// <summary>
/// VOICEVOX のユーザー辞書 <c>/user_dict</c> へ読みを<b>冪等同期</b>する（仕様 W19a）。
/// 表記が無ければ追加・読み/アクセントが違えば更新・同一なら何もしない。<b>削除はしない</b>（手動登録を保護）。
/// <b>完全 fail-tolerant：throw しない</b>（VOICEVOX 未起動・個別 422 でも放送を止めない）。
/// 新 interface は作らず、外部 HTTP は既存 <see cref="IHttpClient"/> シーム越し（CLAUDE.md §3-5）。
/// </summary>
public sealed class VoicevoxUserDict
{
    private readonly Uri _base;
    private readonly IHttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public VoicevoxUserDict(string endpoint, IHttpClient http)
    {
        _base = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : new Uri("http://127.0.0.1:50021/");
        _http = http;
    }

    /// <summary><c>GET /user_dict</c> の 1 ワード（必要なフィールドのみ。他は無視）。</summary>
    private sealed class ExistingWord
    {
        public string Surface { get; set; } = "";
        public string Pronunciation { get; set; } = "";

        [JsonPropertyName("accent_type")]
        public int AccentType { get; set; }
    }

    /// <summary>読み辞書を冪等同期する（仕様 W19a §5.2）。</summary>
    public async Task<PronunciationSyncSummary> SyncAsync(
        IReadOnlyList<PronunciationEntry> entries, CancellationToken ct = default)
    {
        var summary = new PronunciationSyncSummary();
        // GET 前ガード（§5.2 step 1/2）: 停止後は外部 I/O を発行しない・空入力は GET すら出さない。
        if (ct.IsCancellationRequested || entries.Count == 0)
        {
            return summary;
        }

        // 1) 既存辞書を取得。GET 呼び出しとデコードを同一 try で全捕捉（接続不可・非 2xx・応答破損いずれも unreachable）。
        Dictionary<string, ExistingWord> existing;
        try
        {
            var data = await _http.GetAsync(MakeUrl("user_dict", null), headers: null, ct).ConfigureAwait(false);
            // 裸の JSON null も破損扱いで unreachable に落とす（Mac の非 Optional 辞書デコードは null で throw＝§13 W-8）。
            existing = JsonSerializer.Deserialize<Dictionary<string, ExistingWord>>(data, JsonOptions)
                ?? throw new JsonException("user_dict response was bare null");
        }
        catch (Exception)
        {
            summary.Unreachable = true;   // E-PRON-SYNC-UNREACHABLE-001（呼び出し側がログ）。
            return summary;
        }

        // 2) surface（NFKC 正規化）→ (uuid, raw ExistingWord) のマップ。
        //    VOICEVOX は surface を全角化して保存・返却するため、raw 比較では一致しない（§4-4）。読みの正規化は比較時に行う。
        var bySurface = new Dictionary<string, (string Uuid, ExistingWord Word)>();
        foreach (var (uuid, word) in existing)
        {
            bySurface[MatchKey(word.Surface)] = (uuid, word);
        }

        // 3) config 側を NFKC キーで重複排除しつつ差分適用。
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            // 停止後は外部 I/O を即休止（§3-1。throw しない cancellation 観測）。
            if (ct.IsCancellationRequested)
            {
                return summary;
            }

            var key = MatchKey(entry.Surface);
            if (!seen.Add(key))
            {
                continue;   // config 内の表記ゆれ重複は素 continue（先勝ち・カウンタ不変）。
            }

            // 読みは全角カタカナへ正規化＋検証。非カタカナ（空・ひらがな・漢字・英字等）は送らずスキップ。
            var pron = NormalizePronunciation(entry.Pronunciation);
            if (!IsKatakana(pron))
            {
                summary.Failed += 1;   // E-PRON-WORD-REJECTED-001（非カタカナ）。
                continue;
            }

            if (bySurface.TryGetValue(key, out var existingEntry))
            {
                var same = NormalizePronunciation(existingEntry.Word.Pronunciation) == pron
                    && existingEntry.Word.AccentType == entry.AccentType;
                if (same)
                {
                    summary.Skipped += 1;
                    continue;
                }

                // 読み or アクセントが違う → 更新（uuid は GET 由来）。
                try
                {
                    await _http.PutAsync(
                        WordUrl(existingEntry.Uuid, entry, pron), body: null, headers: null, ct).ConfigureAwait(false);
                    summary.Updated += 1;
                }
                catch (Exception)
                {
                    summary.Failed += 1;   // E-PRON-WORD-REJECTED-001（422 等。全捕捉・継続）。
                }
            }
            else
            {
                // 既存に無い → 追加。
                try
                {
                    await _http.PostAsync(
                        WordUrl(null, entry, pron), body: null, headers: null, ct).ConfigureAwait(false);
                    summary.Added += 1;
                }
                catch (Exception)
                {
                    summary.Failed += 1;
                }
            }
        }

        return summary;
    }

    /// <summary>
    /// 追加（<c>POST /user_dict_word</c>）・更新（<c>PUT /user_dict_word/{uuid}</c>）の URL。
    /// VOICEVOX はクエリパラメータ方式（body=null）。surface は config の raw 値、pronunciation は正規化後を送る（§13 W-7）。
    /// </summary>
    private Uri WordUrl(string? uuid, PronunciationEntry entry, string pronunciation)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("surface", entry.Surface),
            new("pronunciation", pronunciation),
            new("accent_type", entry.AccentType.ToString(CultureInfo.InvariantCulture)),   // サーバ必須。常に送る。
        };
        if (entry.WordType is { } wordType)
        {
            query.Add(new("word_type", wordType));
        }
        if (entry.Priority is { } priority)
        {
            query.Add(new("priority", priority.ToString(CultureInfo.InvariantCulture)));
        }
        var path = uuid is null ? "user_dict_word" : $"user_dict_word/{uuid}";
        return MakeUrl(path, query);
    }

    private Uri MakeUrl(string path, IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        var builder = new UriBuilder(new Uri(_base, path));
        if (query is { Count: > 0 })
        {
            var sb = new StringBuilder();
            foreach (var (key, value) in query)
            {
                if (sb.Length > 0)
                {
                    sb.Append('&');
                }
                sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }
            builder.Query = sb.ToString();
        }
        return builder.Uri;
    }

    /// <summary>突合キー：NFKC（全角/半角の差を吸収）。<c>Mr.Children</c> ↔ <c>Ｍｒ．Ｃｈｉｌｄｒｅｎ</c> を一致させる。</summary>
    private static string MatchKey(string surface) => Normalize(surface);

    /// <summary>読みの正規化：半角カナ → 全角カナ（濁点も合成）。</summary>
    private static string NormalizePronunciation(string pronunciation) => Normalize(pronunciation);

    /// <summary>
    /// NFKC（互換分解 ＋ 正準合成）。.NET の <see cref="NormalizationForm.FormKC"/> は半角濁点（ﾄﾞ）→ ド まで
    /// 1 呼び出しで合成する（Mac は <c>precomposedStringWithCompatibilityMapping</c> ＋ <c>…CanonicalMapping</c> の 2 段。§13 W-1）。
    /// </summary>
    private static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC);

    /// <summary>
    /// 全角カタカナ（＋長音「ー」U+30FC・中黒「・」U+30FB）のみか。<b>空は false</b>（§5.1）。
    /// VOICEVOX は非カタカナ読みを 422 で弾く。C# の <see cref="Enumerable.All{T}"/> は空で true を返すため非空ガードを前置する。
    /// </summary>
    private static bool IsKatakana(string text) =>
        !string.IsNullOrEmpty(text) && text.All(c =>
            (c >= 'ァ' && c <= 'ヺ')   // ァ..ヺ（カタカナ）
            || c == '・'                   // ・（中黒）
            || c == 'ー');                 // ー（長音）
}
