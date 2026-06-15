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
/// 番組フォーマットの 1 セグメント（<c>config/program.yaml</c> の <c>segments[]</c> 1 件）。
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
/// 番組フォーマット（<c>config/program.yaml</c> v1 = 明示 <c>segments:</c> 列のパース結果）。
/// W13 でコーナー数 N 駆動の生成（<c>ProgramBlueprint</c> + <c>ProgramPlan</c>, v2 部品宣言）へ移行する。
/// </summary>
/// <param name="Title">番組名（OP/ED 等の表示・選曲コンテキスト・ログ用）。</param>
/// <param name="AnchorDjId">OP/news/ED を読む DJ（<c>djs.yaml</c> の id）。</param>
/// <param name="Segments">宣言順に実行するセグメント列。</param>
public sealed record ProgramFormat(
    string Title,
    string AnchorDjId,
    IReadOnlyList<ProgramSegment> Segments);

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
