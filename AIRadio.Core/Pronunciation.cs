namespace AIRadio.Core;

/// <summary>
/// 読み辞書のエントリ（仕様 W19a）。VOICEVOX ユーザー辞書へ同期する 1 語。
/// <paramref name="Surface"/>（表記）が入力テキストに現れたら、指定の <paramref name="Pronunciation"/>（読み）・
/// <paramref name="AccentType"/> で発音させる。ドメイン型は Core、ローダ／同期実装は Infrastructure（<c>ArtistProfile</c> に倣う）。
/// </summary>
/// <param name="Surface">表記（例: "栄光の架橋" / "Mr.Children"）。</param>
/// <param name="Pronunciation">読み（全角カタカナ。ひらがな・半角カナは VOICEVOX が弾く＝同期側で正規化・検証する）。</param>
/// <param name="AccentType">アクセント型（音が下がるモーラ位置。0 = 平板。VOICEVOX では必須なので未指定でも 0 を送る）。</param>
/// <param name="WordType">品詞種別（任意。PROPER_NOUN / COMMON_NOUN / VERB / ADJECTIVE / SUFFIX）。</param>
/// <param name="Priority">優先度（任意。0–10。未指定時のサーバ既定は 5）。</param>
public sealed record PronunciationEntry(
    string Surface,
    string Pronunciation,
    int AccentType = 0,
    string? WordType = null,
    int? Priority = null);
