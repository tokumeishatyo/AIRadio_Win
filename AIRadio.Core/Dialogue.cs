namespace AIRadio.Core;

/// <summary>番組 DJ のプロフィール（<c>config/djs.yaml</c>）。</summary>
public sealed record DjProfile(string Id, string Name, int SpeakerId, string Persona);

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
/// W6 は基本フィールドのみ。テーマプール・時報リード文・アーティスト特集パラメータは後続スライスで追加する。
/// </summary>
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
    int PlaySeconds = 0)
{
    /// <summary>台本の目標文字数（5 分 × 320 字/分 ≒ 1600 字）。</summary>
    public int TargetCharacters => TargetMinutes * CharsPerMinute;
}
