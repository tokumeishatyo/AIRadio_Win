using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary><c>config/djs.yaml</c> のローダ（DJ プロフィール一覧 → <see cref="DjProfile"/>）。</summary>
public static class DjsConfig
{
    public static IReadOnlyList<DjProfile> FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var djs = dto?.Djs;
        if (djs is null || djs.Count == 0)
        {
            throw ConfigException.MissingField("djs");
        }
        return djs.Select(Map).ToList();
    }

    public static IReadOnlyList<DjProfile> LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static DjProfile Map(DjDto d)
    {
        if (string.IsNullOrEmpty(d.Id))
        {
            throw ConfigException.MissingField("djs[].id");
        }
        if (string.IsNullOrEmpty(d.Name))
        {
            throw ConfigException.MissingField($"djs[{d.Id}].name");
        }
        if (d.SpeakerId is null)
        {
            throw ConfigException.MissingField($"djs[{d.Id}].speaker_id");
        }
        if (string.IsNullOrEmpty(d.Persona))
        {
            throw ConfigException.MissingField($"djs[{d.Id}].persona");
        }
        var (ttsBackend, aquestalkVoice) = ResolveTtsBackend(d.TtsBackend, d.AquestalkVoice, $"djs[{d.Id}]");
        return new DjProfile(d.Id, d.Name, d.SpeakerId.Value, d.Persona, ttsBackend, aquestalkVoice);
    }

    /// <summary>
    /// W-AQT: <c>tts_backend</c>（既定 <c>voicevox</c>）と <c>aquestalk_voice</c> を解決・検証する（djs / guests 共通）。
    /// <c>aquestalk</c> のとき <c>aquestalk_voice</c> 必須、未知の backend 値は fail-fast（<c>E-CFG-MISSING-FIELD-001</c>）。
    /// </summary>
    internal static (string TtsBackend, string? AquesTalkVoice) ResolveTtsBackend(
        string? ttsBackend, string? aquestalkVoice, string idLabel)
    {
        var backend = string.IsNullOrWhiteSpace(ttsBackend) ? "voicevox" : ttsBackend.Trim();
        if (backend != "voicevox" && backend != "aquestalk")
        {
            throw ConfigException.MissingField($"{idLabel}.tts_backend（voicevox|aquestalk のみ）");
        }
        string? voice = null;
        if (backend == "aquestalk")
        {
            if (string.IsNullOrWhiteSpace(aquestalkVoice))
            {
                throw ConfigException.MissingField($"{idLabel}.aquestalk_voice");
            }
            voice = aquestalkVoice.Trim();
        }
        return (backend, voice);
    }

    public sealed class Dto
    {
        public List<DjDto>? Djs { get; set; }
    }

    public sealed class DjDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? SpeakerId { get; set; }
        public string? Persona { get; set; }
        public string? TtsBackend { get; set; }       // W-AQT: voicevox|aquestalk（既定 voicevox）。yaml tts_backend
        public string? AquestalkVoice { get; set; }    // W-AQT: 声種 f1/f2/…（aquestalk のとき必須）。yaml aquestalk_voice
    }
}
