namespace AIRadio.Core;

/// <summary>
/// 番組 DJ のプロフィール（<c>config/djs.yaml</c>）。
/// <paramref name="TtsBackend"/>（既定 <c>"voicevox"</c>）と <paramref name="AquesTalkVoice"/> は W-AQT の任意フィールド
/// （末尾 optional positional・後方互換）。<c>aquestalk</c> のとき <paramref name="AquesTalkVoice"/> に声種（f1/f2/…）を持つ。
/// </summary>
public sealed record DjProfile(
    string Id, string Name, int SpeakerId, string Persona,
    string TtsBackend = "voicevox", string? AquesTalkVoice = null);

/// <summary>会話台本の 1 行（どの DJ が何を喋るか）。</summary>
public sealed record DialogueLine(string DjId, string Text);

/// <summary>コーナー 1 本分の会話台本。</summary>
public sealed record DialogueScript(IReadOnlyList<DialogueLine> Lines)
{
    /// <summary>セリフの合計文字数（長さ確認用）。</summary>
    public int TotalCharacters => Lines.Sum(l => l.Text.Length);
}

/// <summary>コーナーの進行フォーマット。W6 は <see cref="FreeTalk"/> のみ実行（他は後続スライス）。</summary>
public enum CornerFormat
{
    /// <summary>テーマについて DJ 二人が会話し、最後に一曲かける（基本パターン）。</summary>
    FreeTalk,

    /// <summary>架空リスナーのお便りを読み上げ → 感想 → リクエスト曲（W12）。</summary>
    Letter,

    /// <summary>ゲストを迎えてテーマについて会話 → リクエスト曲（W14）。</summary>
    Guest,

    /// <summary>アーティスト特集（専用エンジンで実行、W15）。</summary>
    ArtistFeature,
}

/// <summary>
/// コーナーのテンプレート（<c>config/corners.yaml</c>）。基本パターン: テーマについて DJ 二人が会話し、最後に一曲かける。
/// <see cref="ThemePool"/> でテーマプール（W12）、<see cref="LeadIn"/> で時報リード文（W13.5）に対応。
/// アーティスト特集パラメータは後続スライスで追加する。
/// </summary>
/// <param name="ThemePool">テーマのプール（W12）。空/null なら準備のたび <see cref="Theme"/> 固定、非空なら毎回ランダムに 1 つ選ぶ。</param>
/// <param name="LeadIn">時報リード文テンプレート（W13.5。空/null なら頭出しなし。時刻プレースホルダを含み、発話直前に展開・合成）。</param>
public sealed record CornerTemplate(
    string Id,
    string Title,
    string Theme,
    CornerFormat Format,
    IReadOnlyList<string> DjIds,
    string FallbackTrackUri,
    int TargetMinutes = 5,
    int CharsPerMinute = 320,
    string SongPromptHint = "",
    int Volume = 85,
    int PlaySeconds = 0,
    IReadOnlyList<string>? ThemePool = null,
    string? LeadIn = null,
    ArtistFeatureParams? ArtistFeatureParams = null)
{
    /// <summary>台本の目標文字数（5 分 × 320 字/分 ≒ 1600 字）。</summary>
    public int TargetCharacters => TargetMinutes * CharsPerMinute;
}

/// <summary>
/// アーティスト特集コーナーの追加パラメータ（W15。<see cref="CornerFormat.ArtistFeature"/> のときのみ <c>corners.yaml</c> から構築）。
/// パート別の目標文字数と固定の締め文を持つ。<c>OutroLine</c> の <c>{artist}</c> は準備時に実名展開する。
/// </summary>
/// <param name="IntroTargetChars">導入パートの目標文字数。</param>
/// <param name="GroupIntroTargetChars">グループ紹介パートの目標文字数。</param>
/// <param name="CommentTargetChars">感想（1 回目・長め）の目標文字数。</param>
/// <param name="CommentShortTargetChars">感想（2 回目以降・短め）の目標文字数。<c>&lt; CommentTargetChars</c> をロード時に検証（W15 §13）。</param>
/// <param name="OutroLine">固定の締め（LLM 生成しない。<c>{artist}</c> を含む）。</param>
public sealed record ArtistFeatureParams(
    int IntroTargetChars = 200,
    int GroupIntroTargetChars = 320,
    int CommentTargetChars = 400,
    int CommentShortTargetChars = 240,
    string OutroLine = "以上、{artist}特集でした。");

/// <summary>
/// コーナー準備（<see cref="CornerEngine.PrepareAsync"/>）に渡す番組内コンテキスト（W13.5 §3）。
/// その日の編成・冒頭コーナーの挨拶・他コーナーの時報リード文を運ぶ。
/// </summary>
/// <param name="CastDjIds">その日の出演者（順序付き・先頭＝メイン）。空/null なら <see cref="CornerTemplate.DjIds"/>。</param>
/// <param name="Greeting">冒頭トークのみ非 null（時刻連動の挨拶語）。非 null＝挨拶＋出演者紹介、null＝挨拶抑制で即本題。</param>
/// <param name="LeadIn">その他コーナーの時報リード文テンプレート（発話直前に展開・合成）。</param>
/// <param name="Guest">ゲストコーナー（W14）のみ非 null（選定済みゲスト。<c>djs</c> に居ないため別持ち）。cast 末尾に足す。</param>
/// <param name="JournalContext">冒頭コーナー（W18）のみ非 null/非空（前回までの当週ハイライト振り返り）。台本プロンプトに軽く一言注入する。</param>
public sealed record CornerContext(
    IReadOnlyList<string>? CastDjIds = null,
    string? Greeting = null,
    string? LeadIn = null,
    DjProfile? Guest = null,
    string? JournalContext = null);
