using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>Spotify 設定（PKCE 認証 + Web API）。client_secret は不要（公開クライアント）。</summary>
public sealed record SpotifyConfig(string ClientId, string RedirectUri, string Market, string? DeviceName)
{
    /// <summary>リダイレクト URI から待受ポートを導出する。</summary>
    public ushort LoopbackPort =>
        Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535
            ? (ushort)uri.Port
            : (ushort)5543;

    public static SpotifyConfig FromYaml(string yaml)
    {
        var dto = YamlConfigLoader.Deserialize<Dto>(yaml);
        var clientId = dto?.Spotify?.ClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            throw ConfigException.MissingField("spotify.client_id");
        }

        return new SpotifyConfig(
            ClientId: clientId,
            RedirectUri: dto!.Spotify!.RedirectUri ?? "http://127.0.0.1:5543/callback",
            Market: dto.Spotify.Market ?? "JP",
            DeviceName: dto.Spotify.DeviceName);
    }

    public static SpotifyConfig LoadFile(string path) => FromYaml(File.ReadAllText(path));

    public sealed class Dto
    {
        public SpotifyDto? Spotify { get; set; }

        public sealed class SpotifyDto
        {
            public string? ClientId { get; set; }
            public string? RedirectUri { get; set; }
            public string? Market { get; set; }
            public string? DeviceName { get; set; }
        }
    }
}
