using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>
/// W19a VOICEVOX ユーザー辞書の冪等同期。完全 fail-tolerant（throw しない）・追加/更新のみ・削除なし。
/// NFKC 突合（全角 surface ↔ 半角 config）・全角カタカナ正規化＋検証・accent_type 常時送信・cancellation 観測を検証。
/// </summary>
public class VoicevoxUserDictTests
{
    private const string Endpoint = "http://127.0.0.1:50021/";

    private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>GET /user_dict の空応答。</summary>
    private static byte[] EmptyDict() => Json("{}");

    /// <summary>GET /user_dict に 1 ワードを持たせた応答。</summary>
    private static byte[] DictWith(string uuid, string surface, string pronunciation, int accentType = 0) =>
        Json($"{{\"{uuid}\":{{\"surface\":\"{surface}\",\"pronunciation\":\"{pronunciation}\",\"accent_type\":{accentType}}}}}");

    /// <summary>POST/PUT の応答（生成 UUID の二重引用符付き文字列。本スライスでは未使用）。</summary>
    private static byte[] WordResponse() => Json("\"generated-uuid\"");

    /// <summary>クエリ文字列から 1 パラメータを取り出す（URL デコード。未指定は null）。</summary>
    private static string? QueryValue(Uri url, string key)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            if (Uri.UnescapeDataString(pair[..eq]) == key)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return null;
    }

    private static bool IsGet(Uri url) => url.AbsolutePath == "/user_dict";

    private static VoicevoxUserDict Make(FakeHttpClient http) => new(Endpoint, http);

    [Fact]
    public async Task Sync_EmptyDict_PostsEachEntry_RawSurface_AccentZero()
    {
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());
        var entries = new[]
        {
            new PronunciationEntry("栄光の架橋", "エイコウノカケハシ"),
            new PronunciationEntry("Mr.Children", "ミスターチルドレン"),
        };

        var summary = await Make(http).SyncAsync(entries);

        Assert.Equal(2, summary.Added);
        Assert.Equal(0, summary.Updated + summary.Skipped + summary.Failed);
        Assert.False(summary.Unreachable);

        var posts = http.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p => Assert.Equal("/user_dict_word", p.Url.AbsolutePath));
        // surface は config の raw 値（半角）・accent_type は未指定でも 0 が載る（§13 W-7 / §4-2）。
        Assert.Equal("Mr.Children", QueryValue(posts[1].Url, "surface"));
        Assert.Equal("ミスターチルドレン", QueryValue(posts[1].Url, "pronunciation"));
        Assert.Equal("0", QueryValue(posts[1].Url, "accent_type"));
    }

    [Fact]
    public async Task Sync_NormalizedSurfaceAndReadingMatch_Skips()
    {
        // GET は全角 surface ＋全角カタカナ読み。config は半角 surface ＋同一読み → NFKC 突合で一致＝skip。
        var http = new FakeHttpClient(url =>
            IsGet(url) ? DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン") : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Added + summary.Updated + summary.Failed);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_DifferentReading_PutsToExistingUuid()
    {
        var http = new FakeHttpClient(url =>
            IsGet(url) ? DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスチル") : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.Equal(1, summary.Updated);
        var put = Assert.Single(http.Requests, r => r.Method == "PUT");
        Assert.Equal("/user_dict_word/u1", put.Url.AbsolutePath);
        Assert.Equal("ミスターチルドレン", QueryValue(put.Url, "pronunciation"));
    }

    [Fact]
    public async Task Sync_OnlyPriorityDiffers_DoesNotPut()
    {
        // 既存と読み・accent が同一で priority だけ違う → 比較対象外なので skip（誤 PUT しない）。
        var http = new FakeHttpClient(url =>
            IsGet(url) ? DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン", 0) : WordResponse());

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン", AccentType: 0, Priority: 8) });

        Assert.Equal(1, summary.Skipped);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_DifferentAccentType_Puts()
    {
        var http = new FakeHttpClient(url =>
            IsGet(url) ? DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン", 0) : WordResponse());

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン", AccentType: 3) });

        Assert.Equal(1, summary.Updated);
        var put = Assert.Single(http.Requests, r => r.Method == "PUT");
        Assert.Equal("3", QueryValue(put.Url, "accent_type"));
    }

    [Fact]
    public async Task Sync_HalfWidthKatakanaReading_NormalizedToFullWidthInPost()
    {
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ﾐｽﾀｰﾁﾙﾄﾞﾚﾝ") });

        Assert.Equal(1, summary.Added);
        var post = Assert.Single(http.Requests, r => r.Method == "POST");
        Assert.Equal("ミスターチルドレン", QueryValue(post.Url, "pronunciation"));
    }

    [Fact]
    public async Task Sync_HiraganaReading_FailedNoWrite()
    {
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "みすたー") });

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_IndividualWriteFailure_FailsOnlyThatEntry_NoThrow()
    {
        var http = new FakeHttpClient(url =>
        {
            if (IsGet(url))
            {
                return EmptyDict();
            }
            if (QueryValue(url, "surface") == "ダメ語")
            {
                throw new HttpStatusException(422);
            }
            return WordResponse();
        });

        var summary = await Make(http).SyncAsync(new[]
        {
            new PronunciationEntry("ダメ語", "ダメゴ"),
            new PronunciationEntry("良い語", "ヨイゴ"),
        });

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, http.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task Sync_GetConnectionFailure_Unreachable_NoWrite()
    {
        var http = new FakeHttpClient(_ => throw new HttpRequestException("connection refused"));

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.True(summary.Unreachable);
        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped + summary.Failed);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_GetHttpStatus500_Unreachable()
    {
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.True(summary.Unreachable);
    }

    [Fact]
    public async Task Sync_GetBareNull200_Unreachable_NoWrite()
    {
        // 200 だが本文が裸 null（Mac の非 Optional 辞書デコードは throw → unreachable。§13 W-8）。
        var http = new FakeHttpClient(url => IsGet(url) ? Json("null") : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.True(summary.Unreachable);
        Assert.Single(http.Requests);
        Assert.Equal("GET", http.Requests[0].Method);
    }

    [Fact]
    public async Task Sync_EmptyPronunciation_FailedNoWrite()
    {
        // 空読み → 非カタカナ扱い（IsKatakana 非空ガード）。helper 単体契約のピン留め（§5.1）。
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "") });

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_OnlyWordTypeDiffers_DoesNotPut()
    {
        // 既存と読み・accent が同一で word_type だけ違う → 比較対象外なので skip（§4-5。priority と対称）。
        var http = new FakeHttpClient(url =>
            IsGet(url) ? DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン", 0) : WordResponse());

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン", AccentType: 0, WordType: "PROPER_NOUN") });

        Assert.Equal(1, summary.Skipped);
        Assert.DoesNotContain(http.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task Sync_IndividualPutFailure_FailsOnlyThatEntry_NoThrow()
    {
        // PUT 経路の個別失敗（422）も全捕捉して継続（POST 失敗と対称・throw しない）。
        var http = new FakeHttpClient(url =>
        {
            if (IsGet(url))
            {
                // u1=既存読み違い（→ PUT 更新を要求）。"良い語" は未登録（→ POST 追加）。
                return DictWith("u1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスチル");
            }
            if (url.AbsolutePath.StartsWith("/user_dict_word/", StringComparison.Ordinal))
            {
                throw new HttpStatusException(422);   // PUT のみ失敗。
            }
            return WordResponse();
        });

        var summary = await Make(http).SyncAsync(new[]
        {
            new PronunciationEntry("Mr.Children", "ミスターチルドレン"),   // → PUT（u1）→ 422 → Failed
            new PronunciationEntry("良い語", "ヨイゴ"),                   // → POST → Added
        });

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Updated);
        Assert.Single(http.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task Sync_GetCorruptJson200_Unreachable_OnlyGetRequested()
    {
        // 200 だが本文が JSON オブジェクトでない（デコードが GET と同一 try にあることを強制＝Mac parity）。
        var http = new FakeHttpClient(url => IsGet(url) ? Json("[1,2,3]") : WordResponse());

        var summary = await Make(http).SyncAsync(new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") });

        Assert.True(summary.Unreachable);
        Assert.Single(http.Requests);
        Assert.Equal("GET", http.Requests[0].Method);
    }

    [Fact]
    public async Task Sync_WordTypeAndPriority_OnlyLoadedWhenSpecified()
    {
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        await Make(http).SyncAsync(new[]
        {
            new PronunciationEntry("甲", "コウ", AccentType: 0, WordType: "PROPER_NOUN", Priority: 8),
            new PronunciationEntry("乙", "オツ"),
        });

        var posts = http.Requests.Where(r => r.Method == "POST").ToList();
        var withOptions = posts.Single(p => QueryValue(p.Url, "surface") == "甲");
        var without = posts.Single(p => QueryValue(p.Url, "surface") == "乙");

        Assert.Equal("PROPER_NOUN", QueryValue(withOptions.Url, "word_type"));
        Assert.Equal("8", QueryValue(withOptions.Url, "priority"));
        Assert.Null(QueryValue(without.Url, "word_type"));
        Assert.Null(QueryValue(without.Url, "priority"));
    }

    [Fact]
    public async Task Sync_ConfigDuplicateSurface_PostsOnce_CountersUnchanged()
    {
        // 半角 "Mr.Children" と全角 "Ｍｒ．Ｃｈｉｌｄｒｅｎ" は NFKC 突合で同一 → 後者は先勝ちで素 continue。
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        var summary = await Make(http).SyncAsync(new[]
        {
            new PronunciationEntry("Mr.Children", "ミスターチルドレン"),
            new PronunciationEntry("Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン"),
        });

        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, http.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task Sync_DedupBeforeValidation_SecondDuplicateNonKatakana_NotFailed()
    {
        // 2 件目（重複）の読みが非カタカナでも、重複排除がカタカナ検証より先＝Failed にしない。
        var http = new FakeHttpClient(url => IsGet(url) ? EmptyDict() : WordResponse());

        var summary = await Make(http).SyncAsync(new[]
        {
            new PronunciationEntry("Mr.Children", "ミスターチルドレン"),
            new PronunciationEntry("Ｍｒ．Ｃｈｉｌｄｒｅｎ", "みすたー"),
        });

        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, http.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task Sync_EmptyEntries_DoesNotEvenGet()
    {
        var http = new FakeHttpClient(_ => throw new InvalidOperationException("HTTP は呼ばれないはず"));

        var summary = await Make(http).SyncAsync(Array.Empty<PronunciationEntry>());

        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped + summary.Failed);
        Assert.False(summary.Unreachable);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Sync_CancelledBeforeSync_NoRequests()
    {
        var http = new FakeHttpClient(_ => throw new InvalidOperationException("HTTP は呼ばれないはず"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") }, cts.Token);

        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped + summary.Failed);
        Assert.False(summary.Unreachable);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Sync_CancelledAfterGet_NoWrites_LoopGuard()
    {
        // GET は成功するが、その応答中にキャンセル → ループ先頭ガードが書き込みを止める（Mac Task.isCancelled 相当）。
        using var cts = new CancellationTokenSource();
        var http = new FakeHttpClient(url =>
        {
            if (IsGet(url))
            {
                cts.Cancel();
                return EmptyDict();
            }
            return WordResponse();
        });

        var summary = await Make(http).SyncAsync(
            new[] { new PronunciationEntry("Mr.Children", "ミスターチルドレン") }, cts.Token);

        Assert.Equal(0, summary.Added + summary.Updated + summary.Skipped + summary.Failed);
        Assert.False(summary.Unreachable);
        Assert.Single(http.Requests);
        Assert.Equal("GET", http.Requests[0].Method);
    }

    // --- W19b: MergedEntries（pronunciations.yaml ＋ artists.yaml の reading 統合）---

    [Fact]
    public void MergedEntries_CombinesArtistReadingsAsProperNoun_ExcludesNullReading()
    {
        var pron = new[] { new PronunciationEntry("栄光の架橋", "エイコウノカケハシ") };
        var artists = new[]
        {
            new ArtistProfile("a1", "米津玄師", "ヨネヅケンシ"),
            new ArtistProfile("a2", "あいみょん"),   // reading=null → 対象外
        };

        var merged = VoicevoxUserDict.MergedEntries(pron, artists);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, e => e.Surface == "栄光の架橋");
        Assert.DoesNotContain(merged, e => e.Surface == "あいみょん");
        var yonezu = Assert.Single(merged, e => e.Surface == "米津玄師");
        Assert.Equal("ヨネヅケンシ", yonezu.Pronunciation);
        Assert.Equal(0, yonezu.AccentType);
        Assert.Equal("PROPER_NOUN", yonezu.WordType);
    }

    [Fact]
    public void MergedEntries_PronunciationsWinOnNfkcCollision()
    {
        // 半角 ASCII の pronunciations と全角同義の artist 名は NFKC 突合で衝突 → pronunciations 先勝ち。
        var pron = new[] { new PronunciationEntry("Mr.Children", "ミスチル") };
        var artists = new[] { new ArtistProfile("a1", "Ｍｒ．Ｃｈｉｌｄｒｅｎ", "ミスターチルドレン") };

        var merged = VoicevoxUserDict.MergedEntries(pron, artists);

        var only = Assert.Single(merged);
        Assert.Equal("Mr.Children", only.Surface);      // pronunciations の raw 値
        Assert.Equal("ミスチル", only.Pronunciation);    // 明示指定が勝つ（素の == 突合なら 2 件で落ちる）
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MergedEntries_ExcludesNullOrEmptyReading(string? reading)
    {
        var merged = VoicevoxUserDict.MergedEntries(
            Array.Empty<PronunciationEntry>(),
            new[] { new ArtistProfile("a1", "米津玄師", reading) });

        Assert.Empty(merged);
    }
}
