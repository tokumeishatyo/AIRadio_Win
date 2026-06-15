namespace AIRadio.Core;

/// <summary>
/// 番組セグメントの種別。W7 は <see cref="Opening"/> / <see cref="Talk"/> / <see cref="News"/> / <see cref="Ending"/> の 4 種。
/// 冒頭曲 <c>Song</c>（W10）・アーティスト特集 <c>ArtistFeature</c>（W15）は後続スライスで追加する。
/// </summary>
public enum SegmentKind
{
    /// <summary>オープニング（統一テーマ演出）。</summary>
    Opening,

    /// <summary>トークコーナー（W6 <see cref="CornerEngine"/>。<c>CornerId</c> でテンプレ参照）。</summary>
    Talk,

    /// <summary>ニュース・天気（実データ原稿 + news テーマ演出）。</summary>
    News,

    /// <summary>エンディング（統一テーマ演出）。</summary>
    Ending,
}

/// <summary>
/// 番組フォーマットの 1 セグメント（<c>config/program.yaml</c> の <c>segments[]</c> 1 件）。
/// </summary>
/// <param name="Kind">セグメント種別。</param>
/// <param name="CornerId"><see cref="SegmentKind.Talk"/> のみ必須（<c>corners.yaml</c> の id）。それ以外は null。</param>
/// <param name="Critical">true なら失敗時にスキップせず放送中止（既定の番組では OP に設定）。</param>
public sealed record ProgramSegment(SegmentKind Kind, string? CornerId = null, bool Critical = false);

/// <summary>
/// 番組フォーマット（<c>config/program.yaml</c> v1 = 明示 <c>segments:</c> 列のパース結果）。
/// W13 でコーナー数 N 駆動の生成（<c>ProgramBlueprint</c> + <c>ProgramPlan</c>, v2 部品宣言）へ移行する。
/// </summary>
/// <param name="Title">番組名（OP/ED 等の表示・ログ用）。</param>
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
/// <param name="OpeningAnnouncement">OP の固定口上（W7 は静的。<c>{greeting}</c>/時刻/<c>{first_song}</c> は後続）。</param>
/// <param name="News">news の BGM 演出（tagline 込み。announcement は実行時注入）。</param>
/// <param name="Ending">ED の BGM 演出（tagline なし = いきなり BGM）。</param>
/// <param name="EndingAnnouncement">ED の固定口上（W7 は静的）。</param>
public sealed record BroadcastThemes(
    ThemeConfig Opening,
    string OpeningAnnouncement,
    ThemeConfig News,
    ThemeConfig Ending,
    string EndingAnnouncement);
