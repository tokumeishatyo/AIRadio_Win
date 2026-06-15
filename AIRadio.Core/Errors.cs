namespace AIRadio.Core;

/// <summary>安定エラーコードを持つ AIRadio 共通エラー。形式 E-&lt;CAT3&gt;-&lt;DETAIL&gt;-&lt;NNN&gt;。</summary>
public interface IRadioError
{
    string Code { get; }
}

/// <summary>安定コードを持つ AIRadio 例外の基底。台帳は docs/specs/error-codes.md。</summary>
public abstract class RadioException : Exception, IRadioError
{
    public abstract string Code { get; }

    protected RadioException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Spotify（検索 + Web API 再生）に関するエラー。</summary>
public sealed class SpotifyException : RadioException
{
    public override string Code { get; }

    private SpotifyException(string code, string message) : base(message) => Code = code;

    /// <summary>再生可能な Spotify がない。</summary>
    public static SpotifyException NoDevice() =>
        new("E-SPT-NO-DEVICE-001", "再生可能な Spotify が見つかりません（Spotify アプリを起動してください）");

    /// <summary>Spotify 操作の一般失敗。</summary>
    public static SpotifyException ApiFailed(string detail) =>
        new("E-SPT-API-FAILED-001", $"Spotify 操作に失敗しました: {detail}");

    /// <summary>トークン交換／更新失敗。</summary>
    public static SpotifyException AuthFailed(string detail) =>
        new("E-SPT-AUTH-FAILED-001", $"Spotify 認証に失敗しました（client_id / redirect_uri を確認してください）: {detail}");

    /// <summary>未ログイン（refresh トークンなし）。PKCE 認可が必要。</summary>
    public static SpotifyException AuthRequired() =>
        new("E-SPT-AUTH-REQUIRED-001", "Spotify にログインしてください（spotify-auth で認証）");

    /// <summary>検索 / トラック取得失敗。</summary>
    public static SpotifyException SearchFailed(string detail) =>
        new("E-SPT-SEARCH-FAILED-001", $"Spotify 検索に失敗しました: {detail}");
}

/// <summary>設定（YAML ロード・検証）に関するエラー。</summary>
public sealed class ConfigException : RadioException
{
    public override string Code { get; }

    private ConfigException(string code, string message) : base(message) => Code = code;

    /// <summary>設定の必須フィールド欠落。</summary>
    public static ConfigException MissingField(string field) =>
        new("E-CFG-MISSING-FIELD-001", $"設定の必須フィールドが不足しています: {field}");
}

/// <summary>TTS（VOICEVOX / AquesTalk 等）に関するエラー。</summary>
public sealed class TtsException : RadioException
{
    public override string Code { get; }

    private TtsException(string code, string message) : base(message) => Code = code;

    /// <summary>VOICEVOX に接続できない。</summary>
    public static TtsException Unreachable() =>
        new("E-TTS-UNREACHABLE-001", "VOICEVOX に接続できません（VOICEVOX を起動してください）");

    /// <summary>合成 API がエラー応答／解釈不能。</summary>
    public static TtsException SynthesisFailed(string detail) =>
        new("E-TTS-SYNTHESIS-FAILED-001", $"音声合成に失敗しました: {detail}");
}

/// <summary>
/// LLM（Gemini）に関するエラー。API キー欠落は fail-fast、それ以外は fail-tolerant（コーナー中断 + 静寂、放送は継続）。
/// </summary>
public sealed class LlmException : RadioException
{
    public override string Code { get; }

    private LlmException(string code, string message) : base(message) => Code = code;

    /// <summary>API キーが未設定（<c>llm.local.yaml</c> なし / api_key 空 / プレースホルダのまま）。</summary>
    public static LlmException KeyMissing() =>
        new("E-LLM-KEY-MISSING-001",
            "Gemini API キーが見つかりません（config/llm.local.yaml.sample をコピーして config/llm.local.yaml に api_key を設定してください）");

    /// <summary>generateContent が非 2xx / 通信失敗 / 応答解釈不能。</summary>
    public static LlmException ApiFailed(string detail) =>
        new("E-LLM-API-FAILED-001", $"LLM リクエストに失敗しました: {detail}");

    /// <summary>LLM 応答にテキストがない。</summary>
    public static LlmException EmptyResponse() =>
        new("E-LLM-EMPTY-RESPONSE-001", "LLM の応答にテキストが含まれていません");

    /// <summary>LLM 応答を台本として解釈できない（行数不足）。</summary>
    public static LlmException ScriptParseFailed(string detail) =>
        new("E-LLM-SCRIPT-PARSE-FAILED-001", $"LLM 応答を台本として解釈できませんでした: {detail}");
}

/// <summary>リサーチ（ニュース / 天気）に関するエラー。放送事故ゼロ系のため呼び出し側で握り潰す（fail-tolerant）。</summary>
public sealed class ResearchException : RadioException
{
    public override string Code { get; }

    private ResearchException(string code, string message) : base(message) => Code = code;

    /// <summary>ニュースの取得・解析に失敗。</summary>
    public static ResearchException NewsFetchFailed(string detail) =>
        new("E-NEWS-FETCH-FAILED-001", $"ニュースの取得に失敗しました: {detail}");

    /// <summary>天気予報の取得・解析に失敗。</summary>
    public static ResearchException WeatherFetchFailed(string detail) =>
        new("E-WX-FETCH-FAILED-001", $"天気予報の取得に失敗しました: {detail}");
}

/// <summary>音声再生に関するエラー。</summary>
public sealed class AudioException : RadioException
{
    public override string Code { get; }

    private AudioException(string code, string message, Exception? inner) : base(message, inner) => Code = code;

    /// <summary>音声再生の開始・実行に失敗（不正な WAV / 再生デバイス異常 等）。</summary>
    public static AudioException PlaybackFailed(Exception? inner = null) =>
        new("E-RTM-AUDIO-PLAYBACK-001", "音声の再生に失敗しました", inner);
}

/// <summary>
/// 番組進行（<see cref="BroadcastEngine"/>）に関するエラー。critical セグメント（既定 OP）が実行時エラーで
/// 中断したときに放送を中止する媒体（fail-tolerant な非 critical セグメントはスキップして継続する）。
/// </summary>
public sealed class BroadcastException : RadioException
{
    public override string Code { get; }

    private BroadcastException(string code, string message) : base(message) => Code = code;

    /// <summary>critical セグメントが実行時エラーで中断 → 放送中止。</summary>
    public static BroadcastException SegmentFailed(string detail) =>
        new("E-RTM-SEGMENT-FAILED-001", $"放送セグメントが中断しました: {detail}");
}
