using AIRadio.Core;

namespace AIRadio.Core.Tests;

public class DialogueScriptGeneratorTests
{
    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    [Fact]
    public void Parse_MapsDjNamesToLines_StripsDecoration_AllowsBothColons()
    {
        const string raw =
            "ずんだもん: こんにちは、なのだ。\n" +
            "**四国めたん**：どうも。\n" +              // 強調 + 全角コロン
            "- ずんだもん: 今日は音楽の話なのだ。\n" +   // 箇条書き装飾
            "ナレーション: これは無視される\n" +        // 未登録 DJ 名 → 無視
            "四国めたん: いいですわね。";

        var script = DialogueScriptGenerator.Parse(raw, Djs);

        Assert.Equal(4, script.Lines.Count);
        Assert.Equal(new DialogueLine("zundamon", "こんにちは、なのだ。"), script.Lines[0]);
        Assert.Equal(new DialogueLine("metan", "どうも。"), script.Lines[1]);
        Assert.Equal(new DialogueLine("zundamon", "今日は音楽の話なのだ。"), script.Lines[2]);
        Assert.Equal("metan", script.Lines[3].DjId);
    }

    [Fact]
    public void Parse_TooFewLines_ThrowsScriptParseFailed()
    {
        const string raw = "ずんだもん: 一行だけなのだ。";

        var ex = Assert.Throws<LlmException>(() => DialogueScriptGenerator.Parse(raw, Djs));
        Assert.Equal("E-LLM-SCRIPT-PARSE-FAILED-001", ex.Code);
    }

    [Fact]
    public void MakeRequest_IncludesThemeNamesAndConstraints()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("音楽", request.Prompt);              // テーマ
        Assert.Contains("ずんだもん", request.Prompt);        // DJ 名
        Assert.Contains("DJ名: セリフ", request.Prompt);      // 出力契約
        Assert.Contains("アイドル", request.Prompt);          // 確定曲の紹介指示
        Assert.NotNull(request.System);
        Assert.Contains("放送作家", request.System!);          // system プロンプト
        Assert.DoesNotContain("# 今日の日付と季節", request.Prompt); // dateContext 未指定なら季節節なし
        Assert.Contains("# テーマ", request.Prompt);
        Assert.DoesNotContain("掛け合い", request.Prompt); // banterDirective 未指定なら掛け合い指示なし（W-DLG 後方互換）
    }

    [Fact]
    public void MakeRequest_WithBanterDirective_IncludesItInPrompt()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(
            corner, Djs, song, theme: "音楽", banterDirective: "霊夢が問いかけ、魔理沙が答える掛け合い。");

        Assert.Contains("霊夢が問いかけ、魔理沙が答える掛け合い。", request.Prompt); // W-DLG: config 指示文がそのまま制約に
    }

    [Fact]
    public void MakeRequest_WithoutBanterDirective_DoesNotInject()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.DoesNotContain("掛け合い", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithEmptyBanterDirective_DoesNotInject()
    {
        // 明示的に空文字を渡しても、省略時と同じく非注入（!IsNullOrEmpty ガード）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽", banterDirective: "");

        Assert.DoesNotContain("掛け合い", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithDateContext_InjectsSeasonSectionAndConstraint()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(
            corner, Djs, song, theme: "音楽", dateContext: "今日は6月12日、梅雨の時期です。");

        Assert.Contains("# 今日の日付と季節", request.Prompt);
        Assert.Contains("今日は6月12日、梅雨の時期です。", request.Prompt);
        Assert.Contains("季節や時候の話は、上の日付・季節に合わせる。", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithLetter_ReplacesThemeWithLetter_AndAddsRequestSongIntro()
    {
        var corner = new CornerTemplate(
            "letter", "お便り", "音楽", CornerFormat.Letter,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");
        var letter = new ListenerLetter("梅雨好き", "雨の日が好きです。");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽", letter: letter);

        Assert.Contains("# リスナーからのお便り", request.Prompt);
        Assert.Contains("ラジオネーム: 梅雨好き", request.Prompt);
        Assert.Contains("雨の日が好きです。", request.Prompt);
        Assert.DoesNotContain("# テーマ", request.Prompt);               // letter ありはテーマ節を出さない
        Assert.Contains("梅雨好きさんからのリクエスト曲", request.Prompt); // 曲振りの letter 変種
        Assert.DoesNotContain("テーマの余韻から自然に", request.Prompt);  // free_talk 変種は混ざらない
    }

    [Fact]
    public void MakeRequest_FreeTalk_HasNoLetterArtifacts()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("テーマの余韻から自然に", request.Prompt); // free_talk の曲振り
        Assert.DoesNotContain("リクエスト曲", request.Prompt);     // letter 変種は混ざらない
        Assert.DoesNotContain("お便り", request.Prompt);
        Assert.DoesNotContain("ラジオネーム", request.Prompt);
    }

    [Fact]
    public void MakeRequest_MidProgramCorner_ForbidsProgramEndingPhrases()
    {
        // コーナーは番組途中。番組全体を締めくくる言い方（「ラストナンバー」「また来週」等）を抑止し、
        // 締めは「コーナーを締める」単位であることを明示する（実機で free_talk が番組終了風に話した事象の対策）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽");

        Assert.Contains("番組全体を締めくくる言い方", request.Prompt);
        Assert.Contains("ラストナンバー", request.Prompt); // 禁止例が明示されている
        Assert.Contains("コーナーを締める", request.Prompt); // 締めはコーナー単位
    }

    [Fact]
    public void MakeRequest_WithGreeting_OpeningCorner_IntroducesCast_NotMidProgram()
    {
        // 冒頭トーク（greeting 非 null）: 挨拶 + 番組名 + 出演者紹介。番組の途中分岐・終了風抑止は出さない（W13.5 §4）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽", greeting: "おはようございます");

        Assert.Contains("番組の最初のコーナー", request.Prompt);
        Assert.Contains("おはようございます", request.Prompt);             // 挨拶語
        Assert.Contains("ケイラボAIラジオ", request.Prompt);              // 番組名の名乗り
        Assert.Contains("ずんだもん", request.Prompt);                    // 出演者紹介（names）
        Assert.Contains("『今日の気分』を一言", request.Prompt);            // W16 ①: 今日の気分の一言
        Assert.Contains("具体的な時刻・時間帯は断定しない", request.Prompt); // W16 ①': 時刻・時間帯の断定抑制
        Assert.DoesNotContain("これは番組の途中のコーナー", request.Prompt); // 途中分岐は出さない
        Assert.DoesNotContain("番組全体を締めくくる言い方", request.Prompt); // 冒頭に終了風抑止は付けない
    }

    [Fact]
    public void MakeRequest_WithoutGreeting_KeepsMidProgramAntiShowCloseClause()
    {
        // 非冒頭（greeting null）: W12 の「番組の途中 + 終了風抑止」を完全な文言で維持する（Mac の短縮形に置換しない）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽"); // greeting なし

        Assert.Contains("これは番組の途中のコーナー", request.Prompt);
        Assert.Contains("番組全体を締めくくる言い方", request.Prompt); // 終了風抑止句を維持
        Assert.DoesNotContain("番組の最初のコーナー", request.Prompt);
        Assert.DoesNotContain("『今日の気分』を一言", request.Prompt);     // W16 ①: 冒頭コーナー限定（途中では出さない）
        Assert.DoesNotContain("具体的な時刻・時間帯は断定しない", request.Prompt); // W16 ①': 冒頭コーナー限定
    }

    [Fact]
    public void MakeRequest_WithGuest_AddsExpertFramingConstraints_AndGuestPersona()
    {
        // ゲストコーナー（W14）: 冒頭挨拶・専門家・お礼の制約 + ゲスト persona が # 出演DJ に入る。
        var corner = new CornerTemplate(
            "guest", "ゲストコーナー", "音楽", CornerFormat.Guest,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");
        var guest = new DjProfile("sora", "九州そら", 16, "おっとり穏やか");
        var castWithGuest = new[] { Djs[0], Djs[1], guest }; // CornerEngine と同じく cast 末尾にゲスト

        var request = DialogueScriptGenerator.MakeRequest(corner, castWithGuest, song, theme: "音楽", guest: guest);

        Assert.Contains("ゲスト「九州そら」が冒頭で軽く挨拶", request.Prompt);
        Assert.Contains("ゲスト「九州そら」は「音楽」に詳しい専門家", request.Prompt);
        Assert.Contains("メインがゲスト「九州そら」へお礼", request.Prompt);
        Assert.Contains("おっとり穏やか", request.System!); // ゲスト persona
    }

    [Fact]
    public void MakeRequest_WithoutGuest_NoGuestFraming()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽"); // guest なし

        Assert.DoesNotContain("詳しい専門家", request.Prompt);
        Assert.DoesNotContain("ゲスト", request.Prompt);
    }

    // --- W18: 長期記憶 journalContext（冒頭コーナーのみ・dateContext と同型の注入経路） ---

    [Fact]
    public void MakeRequest_WithJournalContext_AndGreeting_InjectsRecapSectionAndSoftConstraint()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(
            corner, Djs, song, theme: "音楽", greeting: "こんばんは",
            journalContext: "・ゲストに九州そらさんを迎えました。");

        Assert.Contains("# 前回までの番組の振り返り", request.Prompt);          // 振り返りセクション
        Assert.Contains("ゲストに九州そらさんを迎えました。", request.Prompt);    // 中身
        Assert.Contains("軽く一言だけ触れてから本題へ入る", request.Prompt);      // 冒頭の振り返り制約
    }

    [Fact]
    public void MakeRequest_WithoutJournalContext_NoRecap()
    {
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(corner, Djs, song, theme: "音楽", greeting: "こんばんは");

        Assert.DoesNotContain("# 前回までの番組の振り返り", request.Prompt);
        Assert.DoesNotContain("軽く一言だけ触れてから本題へ入る", request.Prompt);
    }

    [Fact]
    public void MakeRequest_WithJournalContext_ButMidProgram_NoSoftConstraint()
    {
        // 途中コーナー（greeting null）: 「軽く一言触れる」制約は冒頭限定なので出さない（実運用では
        // BroadcastEngine が途中に journalContext を渡さないため、この経路自体が発生しない）。
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "zundamon", "metan" }, "spotify:track:fb");
        var song = new TrackInfo("spotify:track:x", "アイドル", "YOASOBI");

        var request = DialogueScriptGenerator.MakeRequest(
            corner, Djs, song, theme: "音楽", journalContext: "・米津玄師さんを特集しました。"); // greeting なし

        Assert.DoesNotContain("軽く一言だけ触れてから本題へ入る", request.Prompt); // 冒頭限定の制約は出ない
    }

    // --- W15: アーティスト特集のパート別プロンプト（位置依存・曲名原文一致・固定締め） ---

    private static IReadOnlyList<TrackInfo> FeatureTracks(int n) =>
        Enumerable.Range(1, n).Select(i => new TrackInfo($"u{i}", $"曲{i}", "米津玄師")).ToList();

    [Fact]
    public void ArtistFeature_Intro_DeclaresFeature_DefersSongs_HasAntiShowClose()
    {
        var request = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.Intro(), "米津玄師", Djs, targetCharacters: 200);

        Assert.Contains("「導入」", request.Prompt);
        Assert.Contains("米津玄師", request.Prompt);
        Assert.Contains("まだ曲名には触れない", request.Prompt);
        Assert.Contains("番組全体を締めくくる言い方", request.Prompt); // 途中＝終了風抑止（§18-7 Win 長形）
        Assert.Contains("ラストナンバー", request.Prompt);
        Assert.Contains("放送作家", request.System!);
    }

    [Fact]
    public void ArtistFeature_FirstGroupIntro_IsPlain_NoContinuityNoLastMarker()
    {
        var request = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.GroupIntro(FeatureTracks(3), Index: 0, Total: 3), "米津玄師", Djs, targetCharacters: 320);

        Assert.Contains("「曲1」（米津玄師）", request.Prompt);          // 曲名・アーティスト名を原文で列挙
        Assert.Contains("一字一句そのまま", request.Prompt);            // 捏造防止
        Assert.Contains("各曲に聴きどころを軽く添え", request.Prompt);  // 1 回目は素直
        Assert.DoesNotContain("すでに進行中", request.Prompt);
        Assert.DoesNotContain("最後はこの", request.Prompt);
    }

    [Fact]
    public void ArtistFeature_MiddleGroupIntro_HasContinuity_NotLast()
    {
        var request = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.GroupIntro(FeatureTracks(3), Index: 1, Total: 3), "米津玄師", Djs, targetCharacters: 320);

        Assert.Contains("すでに進行中", request.Prompt);
        Assert.Contains("引き続き", request.Prompt);
        Assert.Contains("新しく始めるような言い方", request.Prompt);    // 「続いては〜特集」禁止（fix2）
        Assert.Contains("やり直さず", request.Prompt);
        Assert.DoesNotContain("最後はこの", request.Prompt);            // まだ最後ではない
    }

    [Fact]
    public void ArtistFeature_LastGroupIntro_IsMarkedLast_WithTrackCount()
    {
        var oneTrack = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.GroupIntro(FeatureTracks(1), Index: 2, Total: 3), "米津玄師", Djs, targetCharacters: 320);
        Assert.Contains("最後はこの 1 曲", oneTrack.Prompt);            // 曲数に応じたラスト明示（前後空白あり）

        var twoTracks = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.GroupIntro(FeatureTracks(2), Index: 1, Total: 2), "米津玄師", Djs, targetCharacters: 320);
        Assert.Contains("最後はこの 2 曲", twoTracks.Prompt);
    }

    [Fact]
    public void ArtistFeature_SingleGroupIntro_K3_IsPlain_NotMarkedLast()
    {
        // K=3（唯一グループ）は index 0 / total 1 ＝ 1 回目扱い（isFirst）。ラスト明示は付けない。
        var request = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.GroupIntro(FeatureTracks(3), Index: 0, Total: 1), "米津玄師", Djs, targetCharacters: 320);

        Assert.Contains("各曲に聴きどころを軽く添え", request.Prompt);
        Assert.DoesNotContain("最後はこの", request.Prompt);
        Assert.DoesNotContain("すでに進行中", request.Prompt);
    }

    [Fact]
    public void ArtistFeature_Comment_ShorterVsLong()
    {
        var longComment = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.Comment(Shorter: false), "米津玄師", Djs, targetCharacters: 400);
        Assert.Contains("自由に話す", longComment.Prompt);
        Assert.Contains("次の曲紹介には踏み込まない", longComment.Prompt);

        var shortComment = DialogueScriptGenerator.MakeArtistFeatureRequest(
            new ArtistFeaturePart.Comment(Shorter: true), "米津玄師", Djs, targetCharacters: 240);
        Assert.Contains("前の感想より短く", shortComment.Prompt);
    }
}
