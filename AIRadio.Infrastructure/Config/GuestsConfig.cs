using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// <c>config/guests.yaml</c> のローダ（ゲストコーナーのゲストプール → <see cref="DjProfile"/>。W14）。
/// 構造は <c>djs.yaml</c> と同形（<c>guests:</c> キー → id / name / speaker_id / persona）。レギュラーとは別ファイルで管理する。
/// fail-fast は <c>guests</c> 空 / 各 id / name / speaker_id 欠落のみ（<c>E-CFG-MISSING-FIELD-001</c>）。
/// <b>persona は任意（既定 ""）＝<see cref="DjsConfig"/> と異なる</b>（Mac <c>GuestsConfigLoader</c> 一致）。
/// </summary>
public static class GuestsConfig
{
    public static IReadOnlyList<DjProfile> FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var guests = dto?.Guests;
        if (guests is null || guests.Count == 0)
        {
            throw ConfigException.MissingField("guests");
        }
        return guests.Select(Map).ToList();
    }

    public static IReadOnlyList<DjProfile> LoadFile(string path) => FromYaml(File.ReadAllText(path));

    private static DjProfile Map(GuestDto g)
    {
        if (string.IsNullOrEmpty(g.Id))
        {
            throw ConfigException.MissingField("guests[].id");
        }
        if (string.IsNullOrEmpty(g.Name))
        {
            throw ConfigException.MissingField($"guests[{g.Id}].name");
        }
        if (g.SpeakerId is null)
        {
            throw ConfigException.MissingField($"guests[{g.Id}].speaker_id");
        }
        // persona は任意（既定 ""）。レギュラー（djs）と違い必須にしない。
        var (ttsBackend, aquestalkVoice) = DjsConfig.ResolveTtsBackend(g.TtsBackend, g.AquestalkVoice, $"guests[{g.Id}]");
        return new DjProfile(g.Id, g.Name, g.SpeakerId.Value, g.Persona ?? "", ttsBackend, aquestalkVoice);
    }

    public sealed class Dto
    {
        public List<GuestDto>? Guests { get; set; }
    }

    public sealed class GuestDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? SpeakerId { get; set; }
        public string? Persona { get; set; }
        public string? TtsBackend { get; set; }       // W-AQT: voicevox|aquestalk（既定 voicevox）。yaml tts_backend
        public string? AquestalkVoice { get; set; }    // W-AQT: 声種 f1/f2/…（aquestalk のとき必須）。yaml aquestalk_voice
    }
}
