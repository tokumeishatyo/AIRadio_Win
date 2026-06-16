using System.Globalization;

namespace AIRadio.Core;

/// <summary>
/// 番組セグメントの種別。W7 は <see cref="Opening"/> / <see cref="Song"/> / <see cref="Talk"/> /
/// <see cref="News"/> / <see cref="Ending"/> の 5 種。アーティスト特集 <c>ArtistFeature</c>（W15）は後続で追加する。
/// </summary>
public enum SegmentKind
{
    /// <summary>オープニング（統一テーマ演出）。</summary>
    Opening,

    /// <summary>冒頭曲（OP 直後の「本日の1曲目」。再生中に次セグメントを裏で準備し無音を消す）。</summary>
    Song,

    /// <summary>トークコーナー（W6 <see cref="CornerEngine"/>。<c>CornerId</c> でテンプレ参照）。</summary>
    Talk,

    /// <summary>ニュース・天気（実データ原稿 + news テーマ演出）。</summary>
    News,

    /// <summary>エンディング（統一テーマ演出）。</summary>
    Ending,
}

/// <summary>
/// 冒頭曲セグメントの設定（トークのない単曲。OP 直後の「本日の1曲目」）。曲は LLM 選曲（プレフライト済み）し、
/// 全滅時は <see cref="FallbackTrackUri"/> に倒す。
/// </summary>
/// <param name="FallbackTrackUri">選曲全滅・LLM 不調時のフォールバック曲（<c>spotify:track:&lt;ID&gt;</c> に正規化済み）。</param>
/// <param name="PromptHint">選曲プロンプトのヒント。</param>
/// <param name="Volume">再生音量 %（0-100）。</param>
/// <param name="PlaySeconds">頭から何秒かけるか。0 = フル再生（終端を検知して次へ）。</param>
public sealed record SongSegmentSpec(
    string FallbackTrackUri,
    string PromptHint = "",
    int Volume = 100,
    int PlaySeconds = 0);

/// <summary>
/// 番組の 1 セグメント。<see cref="ProgramPlan"/> がコーナー数 N から決定論的に生成する（W13, v2 部品宣言）。
/// </summary>
/// <param name="Kind">セグメント種別。</param>
/// <param name="CornerId"><see cref="SegmentKind.Talk"/> のみ必須（<c>corners.yaml</c> の id）。</param>
/// <param name="Critical">true なら失敗時にスキップせず放送中止（既定の番組では OP に設定）。</param>
/// <param name="Song"><see cref="SegmentKind.Song"/> のみ必須（冒頭曲の設定）。</param>
/// <param name="DjId"><see cref="SegmentKind.News"/> の専任読み手（<c>djs.yaml</c> の id。未指定は anchor が読む）。</param>
public sealed record ProgramSegment(
    SegmentKind Kind,
    string? CornerId = null,
    bool Critical = false,
    SongSegmentSpec? Song = null,
    string? DjId = null);

/// <summary>
/// 番組の長さ（仕様 s13/w13 §1）。トークコーナー（<c>free_talk</c>）の本数で数える
/// （お便り / ニュース / OP / 冒頭曲 / ED は含めない）。Mac の連想値 enum <c>.corners(Int)</c> / <c>.endless</c> を、
/// 連想値 enum を持たない C# では値等価な <c>readonly record struct</c> で再現する（§3-5、新クラスを増やさない）。
/// <para><b>エンドレスは Core にのみ存在し UI には出さない</b>（ユーザー決定 2026-06-16。トーク 10/20/30 で十分長い）。
/// Mac パリティと「ED で終了」の合成 ED ロジック共有のため移植・テストは維持する。</para>
/// </summary>
public readonly record struct ProgramLength
{
    /// <summary>エンドレス（本編無限ループ・ED なし）か。</summary>
    public bool IsEndless { get; }

    /// <summary>トークコーナー本数（<see cref="IsEndless"/> のときは未使用＝0）。</summary>
    public int Corners { get; }

    private ProgramLength(bool isEndless, int corners)
    {
        IsEndless = isEndless;
        Corners = corners;
    }

    /// <summary>トーク N 本の有限番組。</summary>
    public static ProgramLength FromCorners(int count) => new(false, count);

    /// <summary>エンドレス番組。</summary>
    public static ProgramLength Endless { get; } = new(true, 0);

    /// <summary>永続化 / YAML 用の文字列表現（<c>"10"</c> / <c>"endless"</c>。Mac <c>rawValue</c> 一致）。</summary>
    public string RawValue => IsEndless ? "endless" : Corners.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>"endless"</c> または 0 以上の整数のみ受ける（不正は false）。Mac <c>init?(rawValue:)</c> 一致。
    /// 空文字・負数・小数・非数値（<c>"yes"</c> 等）はすべて false（呼び出し側で fail-fast / 既定へ）。
    /// </summary>
    public static bool TryParse(string? raw, out ProgramLength length)
    {
        if (raw == "endless")
        {
            length = Endless;
            return true;
        }
        // 前後空白は許容しない（Mac `Int(rawValue)` は空白付きを nil にする＝厳格パリティ）。符号は許容。
        if (int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count) && count >= 0)
        {
            length = FromCorners(count);
            return true;
        }
        length = default;
        return false;
    }
}

/// <summary>
/// 番組の部品宣言（<c>config/program.yaml</c> v2）。セグメント列は <see cref="ProgramPlan"/> がここから決定論的に生成する
/// （w13 §1/§4）。曜日替わり編成・ゲスト・特集・ジャーナルは後続スライス（W13.5/W14/W15/W18）で追加し、本型には持ち込まない。
/// </summary>
/// <param name="Title">番組名（OP/ED 等の表示・選曲コンテキスト・ログ用）。</param>
/// <param name="AnchorDjId">OP / news / ED を読む DJ（<c>djs.yaml</c> の id）。</param>
/// <param name="DefaultLength">メニュー「番組の長さ」の既定値（<c>program.yaml</c> の <c>default_length</c>）。</param>
/// <param name="OpeningCritical">OP 失敗で放送を中止するか（既定 true、Windows 踏襲）。</param>
/// <param name="Song">冒頭曲（OP 直後の 1 曲）。</param>
/// <param name="TalkCornerId">トークコーナー（<c>corners.yaml</c> の id）。番組の長さ N はこのコーナーの本数。</param>
/// <param name="LetterCornerId">お便りコーナー（<c>corners.yaml</c> の id）。</param>
/// <param name="NewsDjId">ニュースの読み手（null なら anchor）。</param>
public sealed record ProgramBlueprint(
    string Title,
    string AnchorDjId,
    ProgramLength DefaultLength,
    bool OpeningCritical,
    SongSegmentSpec Song,
    string TalkCornerId,
    string LetterCornerId,
    string? NewsDjId = null);

/// <summary>
/// コーナー数 N から決定論的に生成される番組（仕様 s13/w13 §1）。
/// 構成: <c>opening → song → 本編 → ending</c>。本編はトーク 2 本ごとに「お便り → ニュース」を挿入
/// （= <c>talk, talk, letter, news</c> の繰り返し）。N 奇数の端数トークの後はお便りを挟まず ED へ。
/// エンドレスは本編を無限に繰り返し ED なし（Core のみ・UI 非露出。<see cref="ProgramLength"/>）。
/// </summary>
public sealed class ProgramPlan
{
    public ProgramBlueprint Blueprint { get; }
    public ProgramLength Length { get; }

    public ProgramPlan(ProgramBlueprint blueprint, ProgramLength length)
    {
        Blueprint = blueprint;
        Length = length;
    }

    public string Title => Blueprint.Title;
    public string AnchorDjId => Blueprint.AnchorDjId;

    /// <summary>
    /// 総セグメント数（エンドレスは null。UI の「n/全体」表示用）。
    /// 有限: OP + song + 本編（ペア×4 + 端数）+ ED = <c>2 + (N/2)*4 + (N%2) + 1</c>。
    /// </summary>
    public int? TotalSegmentCount
    {
        get
        {
            if (Length.IsEndless)
            {
                return null;
            }
            var n = Length.Corners;
            return 2 + (n / 2) * 4 + (n % 2) + 1;
        }
    }

    /// <summary>index 番目のセグメント（0 始まり）。有限番組は ED の次で null、エンドレスは常に非 null。</summary>
    public ProgramSegment? Segment(int index)
    {
        if (index < 0)
        {
            return null;
        }
        if (index == 0)
        {
            return new ProgramSegment(SegmentKind.Opening, Critical: Blueprint.OpeningCritical);
        }
        if (index == 1)
        {
            return new ProgramSegment(SegmentKind.Song, Song: Blueprint.Song);
        }
        return BodySegment(index - 2);
    }

    /// <summary>本編 body の index → セグメント（<c>talk, talk, letter, news</c> の繰り返し + 端数 + ED）。</summary>
    private ProgramSegment? BodySegment(int body)
    {
        if (Length.IsEndless)
        {
            return Pattern(body % 4);
        }
        var n = Length.Corners;
        var pairBody = (n / 2) * 4;
        var remainder = n % 2;
        if (body < pairBody)
        {
            return Pattern(body % 4);
        }
        if (remainder == 1 && body == pairBody)
        {
            return new ProgramSegment(SegmentKind.Talk, Blueprint.TalkCornerId);
        }
        if (body == pairBody + remainder)
        {
            return new ProgramSegment(SegmentKind.Ending);
        }
        return null;
    }

    /// <summary>本編パターン <c>talk, talk, letter, news</c> の 1 要素。</summary>
    private ProgramSegment Pattern(int position) => position switch
    {
        0 or 1 => new ProgramSegment(SegmentKind.Talk, Blueprint.TalkCornerId),
        2 => new ProgramSegment(SegmentKind.Talk, Blueprint.LetterCornerId),
        _ => new ProgramSegment(SegmentKind.News, DjId: Blueprint.NewsDjId),
    };
}

/// <summary>
/// 放送中の操作ハンドル（「ED で終了」。UI スレッドから安全に呼べる。仕様 s13/w13 §2）。
/// Mac <c>BroadcastControl</c>（<c>NSLock</c>）の移植で、<c>lock</c> ガードで直列化する。
/// </summary>
public sealed class BroadcastControl
{
    private readonly object _lock = new();
    private bool _ending;

    /// <summary>「ED で終了」要求。現セグメント（+ 準備済みの直後トーク）を流したら ED で締める。</summary>
    public void RequestEnding()
    {
        lock (_lock) { _ending = true; }
    }

    public bool IsEndingRequested
    {
        get { lock (_lock) { return _ending; } }
    }
}

/// <summary>
/// OP / news / ED のテーマ演出設定束（<c>config/themes.yaml</c>）。W7 は anchor が読む単一・フラット形
/// （曜日替わり DJ・<c>by_dj</c> は W13.5）。news の announcement は実行時のニュース原稿で差し替えるため
/// <see cref="News"/> は staging（+tagline）のみを持つ。
/// </summary>
/// <param name="Opening">OP の BGM 演出（tagline 込み）。</param>
/// <param name="OpeningAnnouncement">
/// OP の固定口上（<c>{first_song}</c> を含むと冒頭曲の曲振り、時刻プレースホルダ（<c>{greeting}</c> 等, W8）を含むと
/// 発話直前に実時刻が入る）。
/// </param>
/// <param name="News">news の BGM 演出（tagline 込み。announcement は実行時注入）。</param>
/// <param name="Ending">ED の BGM 演出（tagline なし = いきなり BGM）。</param>
/// <param name="EndingAnnouncement">ED の固定口上。</param>
/// <param name="Greetings">時間帯挨拶（<c>{greeting}</c> の値。OP/news/ED の発話直前展開で共有, W8）。</param>
public sealed record BroadcastThemes(
    ThemeConfig Opening,
    string OpeningAnnouncement,
    ThemeConfig News,
    ThemeConfig Ending,
    string EndingAnnouncement,
    Greetings Greetings);
