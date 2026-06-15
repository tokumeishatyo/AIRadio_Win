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

/// <summary>音声再生に関するエラー。</summary>
public sealed class AudioException : RadioException
{
    public override string Code { get; }

    private AudioException(string code, string message, Exception? inner) : base(message, inner) => Code = code;

    /// <summary>音声再生の開始・実行に失敗（不正な WAV / 再生デバイス異常 等）。</summary>
    public static AudioException PlaybackFailed(Exception? inner = null) =>
        new("E-RTM-AUDIO-PLAYBACK-001", "音声の再生に失敗しました", inner);
}
