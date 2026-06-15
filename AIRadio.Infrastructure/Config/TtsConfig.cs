using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>VOICEVOX 接続設定（config/tts.yaml）。</summary>
public sealed record TtsConfig(string Endpoint, string Credit, double PlaybackVolume, double SpeedScale)
{
    /// <summary>YAML 文字列から構築（必須欠落は fail-fast、数値はクランプ）。</summary>
    public static TtsConfig FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var endpoint = dto?.Voicevox?.Endpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            throw ConfigException.MissingField("voicevox.endpoint");
        }

        return new TtsConfig(
            Endpoint: endpoint,
            Credit: dto!.Voicevox!.Credit ?? "",
            PlaybackVolume: Clamp(dto.PlaybackVolume ?? 1.0, 0.0, 1.0),
            SpeedScale: Clamp(dto.SpeedScale ?? 1.0, 0.5, 2.0));
    }

    /// <summary>ファイルパスから読み込む。</summary>
    public static TtsConfig LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);

    /// <summary>YAML 構造に対応する DTO（snake_case）。</summary>
    public sealed class Dto
    {
        public VoicevoxDto? Voicevox { get; set; }
        public double? PlaybackVolume { get; set; }
        public double? SpeedScale { get; set; }

        public sealed class VoicevoxDto
        {
            public string? Endpoint { get; set; }
            public string? Credit { get; set; }
        }
    }
}
