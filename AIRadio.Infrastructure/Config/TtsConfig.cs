using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>VOICEVOX 接続設定（config/tts.yaml）。<paramref name="VoicevoxExePath"/> は任意（W-Win。自動起動用・未設定は探索）。</summary>
public sealed record TtsConfig(
    string Endpoint, string Credit, double PlaybackVolume, double SpeedScale, string? VoicevoxExePath = null,
    string AquesTalkVoicesDir = "native/aqtk1", string AquesTalkDictDir = "native/aqk2k", int AquesTalkSpeed = 100)
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
            SpeedScale: Clamp(dto.SpeedScale ?? 1.0, 0.5, 2.0),
            VoicevoxExePath: string.IsNullOrWhiteSpace(dto.Voicevox!.ExePath) ? null : dto.Voicevox.ExePath,
            // W-AQT: AquesTalk1 関連（非機密。声種DLL/辞書の親フォルダ・発話速度[%]）。欠落時は既定。
            AquesTalkVoicesDir: string.IsNullOrWhiteSpace(dto.Aquestalk?.VoicesDir) ? "native/aqtk1" : dto.Aquestalk!.VoicesDir!.Trim(),
            AquesTalkDictDir: string.IsNullOrWhiteSpace(dto.Aquestalk?.DictDir) ? "native/aqk2k" : dto.Aquestalk!.DictDir!.Trim(),
            AquesTalkSpeed: (int)Clamp(dto.Aquestalk?.Speed ?? 100, 50, 300));
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
        public AquestalkDto? Aquestalk { get; set; }   // W-AQT: aquestalk: 節（非機密）。

        public sealed class VoicevoxDto
        {
            public string? Endpoint { get; set; }
            public string? Credit { get; set; }
            public string? ExePath { get; set; }   // voicevox.exe_path（任意・W-Win 自動起動用）。
        }

        public sealed class AquestalkDto
        {
            public string? VoicesDir { get; set; }   // aquestalk.voices_dir（声種DLL 親フォルダ）。
            public string? DictDir { get; set; }      // aquestalk.dict_dir（AqKanji2Koe.dll + aq_dic/ の親）。
            public int? Speed { get; set; }           // aquestalk.speed（発話速度[%] 50-300）。
        }
    }
}
