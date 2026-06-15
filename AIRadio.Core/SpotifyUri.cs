namespace AIRadio.Core;

/// <summary>Spotify トラック URI のユーティリティ。</summary>
public static class SpotifyUri
{
    /// <summary>`spotify:track:&lt;ID&gt;` / 共有 URL / 裸 ID から track ID を取り出す。</summary>
    public static string TrackId(string raw)
    {
        const string prefix = "spotify:track:";
        if (raw.StartsWith(prefix, StringComparison.Ordinal))
        {
            return raw[prefix.Length..];
        }

        var index = raw.IndexOf("/track/", StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = raw[(index + "/track/".Length)..];
            var end = rest.IndexOfAny(['?', '/']);
            return end >= 0 ? rest[..end] : rest;
        }

        return raw;
    }

    /// <summary>任意形式を `spotify:track:&lt;ID&gt;` に正規化する。</summary>
    public static string NormalizeTrack(string raw) => $"spotify:track:{TrackId(raw)}";
}
