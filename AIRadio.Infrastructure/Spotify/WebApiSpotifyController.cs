using System.Text.Json;
using System.Text.Json.Serialization;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// Spotify Web API でローカル/任意デバイスの再生を制御する <see cref="ISpotifyController"/> 実装。
/// <see cref="PlayAsync"/> は再生キューを指定 URI だけに置き換えてアトミックに再生する（前曲のブリップが起きない）。
/// アクセストークンは <see cref="ISpotifyTokenProvider"/>（PKCE 認証）から取得する。
/// </summary>
public sealed class WebApiSpotifyController : ISpotifyController
{
    private const string PlayerBase = "https://api.spotify.com/v1/me/player";

    private readonly ISpotifyTokenProvider _auth;
    private readonly IHttpClient _http;
    private readonly double _retryDelaySeconds;
    private readonly string? _preferredDeviceName;

    public WebApiSpotifyController(
        ISpotifyTokenProvider auth,
        IHttpClient http,
        double retryDelaySeconds = 1.0,
        string? preferredDeviceName = null)
    {
        _auth = auth;
        _http = http;
        _retryDelaySeconds = retryDelaySeconds;
        _preferredDeviceName = preferredDeviceName;
    }

    public async Task PlayAsync(string uri, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        var body = JsonSerializer.SerializeToUtf8Bytes(new PlayBody(new[] { uri }));
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var deviceId = await ActiveDeviceIdAsync(token, ct).ConfigureAwait(false);
            var url = new Uri($"{PlayerBase}/play?device_id={Uri.EscapeDataString(deviceId)}");
            try
            {
                await _http.PutAsync(url, body, JsonAuth(token), ct).ConfigureAwait(false);
                return;
            }
            catch (HttpStatusException ex) when (ex.StatusCode == 404 && attempt < maxAttempts)
            {
                // デバイスを長時間操作していないと登録が stale になり、device_id 指定でも 404 が返る。
                // transfer playback でデバイスを起こしてから再試行する。
                try
                {
                    await TransferPlaybackAsync(deviceId, token, ct).ConfigureAwait(false);
                }
                catch
                {
                    // 起こせなくても再試行は行う（ベストエフォート）。
                }
                await Task.Delay(TimeSpan.FromSeconds(_retryDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SpotifyException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw SpotifyException.ApiFailed(ex.Message);
            }
        }
        // 到達不能の保険（Mac 構造に合わせて残置）。最終試行の 404 は再試行ガードを外れ
        // 直前の catch で ApiFailed として送出されるため、ここには来ない。
        throw SpotifyException.NoDevice();
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        // 後始末（完全静寂）。既に停止済み（曲の自然終了直後など）は Spotify が 403 を返すが、
        // 結果は同じ「無音」なので握り潰す（CLAUDE.md §3-1 ベストエフォート）。
        try
        {
            var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
            await _http.PutAsync(new Uri($"{PlayerBase}/pause"), null, Bearer(token), ct).ConfigureAwait(false);
        }
        catch
        {
            // 既に停止 / デバイスなし / 認証切れ等。後始末なので伝播させない。
        }
    }

    public async Task SetVolumeAsync(int percent, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        var clamped = Math.Max(0, Math.Min(100, percent));
        var url = new Uri($"{PlayerBase}/volume?volume_percent={clamped}");
        await SendAsync(url, token, ct).ConfigureAwait(false);
    }

    public async Task SeekAsync(int seconds, CancellationToken ct = default)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        // 64bit に昇格してからミリ秒換算（Mac の 64bit Int 演算と一致。32bit のままだと大入力でオーバーフローし負値になる）。
        var url = new Uri($"{PlayerBase}/seek?position_ms={(long)Math.Max(0, seconds) * 1000}");
        await SendAsync(url, token, ct).ConfigureAwait(false);
    }

    public async Task<PlayerState> PlayerStateAsync(CancellationToken ct = default)
    {
        var playback = await CurrentPlaybackAsync(ct).ConfigureAwait(false);
        if (playback is null)
        {
            return new PlayerState(PlaybackState.Stopped);
        }
        var state = playback.IsPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        return new PlayerState(
            state,
            playback.Item?.Uri,
            (playback.ProgressMs ?? 0) / 1000.0,
            // 曲長は同一スナップショットから返す（別リクエストで取り直すと直前の曲の値を掴む = stale, S12 fix）。
            (playback.Item?.DurationMs ?? 0) / 1000.0);
    }

    public async Task<double> CurrentTrackDurationSecondsAsync(CancellationToken ct = default)
    {
        var playback = await CurrentPlaybackAsync(ct).ConfigureAwait(false);
        return (playback?.Item?.DurationMs ?? 0) / 1000.0;
    }

    // MARK: - 内部

    /// <summary>ボディなし PUT（volume / seek）。HTTP 失敗は ApiFailed に包む。</summary>
    private async Task SendAsync(Uri url, string token, CancellationToken ct)
    {
        try
        {
            await _http.PutAsync(url, null, Bearer(token), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SpotifyException.ApiFailed(ex.Message);
        }
    }

    /// <summary><c>GET /v1/me/player</c>。204/空ボディ（アクティブデバイスなし）は null。</summary>
    private async Task<PlaybackResponse?> CurrentPlaybackAsync(CancellationToken ct)
    {
        var token = await _auth.ValidAccessTokenAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await _http.GetAsync(new Uri(PlayerBase), Bearer(token), ct).ConfigureAwait(false);
            if (data.Length == 0)
            {
                return null; // 204 No Content = アクティブデバイスなし
            }
            return JsonSerializer.Deserialize<PlaybackResponse>(data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SpotifyException.ApiFailed(ex.Message);
        }
    }

    /// <summary>再生先デバイスへの transfer playback（スリープ状態のデバイスを起こす。play=false = 即再生しない）。</summary>
    private async Task TransferPlaybackAsync(string deviceId, string token, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new TransferBody(new[] { deviceId }, false));
        await _http.PutAsync(new Uri(PlayerBase), body, JsonAuth(token), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 再生先デバイスの選択。**この PC の Spotify 以外に勝手に飛ばさない**:
    /// <list type="bullet">
    /// <item><paramref name="_preferredDeviceName"/> 指定時: 名前一致のみ（なければ NoDevice）。</item>
    /// <item>未指定時: <b>この PC（Spotify 表示名＝コンピュータ名 <see cref="Environment.MachineName"/>）の Computer を最優先</b>。
    /// 一致が無ければ type=Computer のデバイス（アクティブ優先 → 先頭）。Computer がなければ NoDevice。</item>
    /// </list>
    /// <para>ホスト名優先（Win 独自・Mac には無い）は、同一アカウントに別 PC（例: Mac）もログインして Computer が複数見える
    /// 環境で、設定なしでもローカル機で鳴らすため（実機で別デバイス再生を確認 2026-06-18）。一致しなければ Mac と同一の
    /// アクティブ→先頭ロジックに退行するので現状より悪くならない。<c>devices.first</c> への安易なフォールバック（スマホ等への
    /// Connect 転送事故）は引き続き行わない。</para>
    /// </summary>
    private async Task<string> ActiveDeviceIdAsync(string token, CancellationToken ct)
    {
        try
        {
            var data = await _http.GetAsync(new Uri($"{PlayerBase}/devices"), Bearer(token), ct).ConfigureAwait(false);
            var devices = JsonSerializer.Deserialize<DevicesResponse>(data)?.Devices ?? new List<Device>();

            if (_preferredDeviceName is not null)
            {
                var named = devices.FirstOrDefault(d => d.Name == _preferredDeviceName)
                    ?? throw SpotifyException.NoDevice();
                return named.Id;
            }

            var computers = devices.Where(d => d.Type == "Computer").ToList();
            // この PC（表示名＝コンピュータ名）を最優先。複数 Computer が見えてもローカル機で鳴らす。
            var thisPc = computers.FirstOrDefault(
                d => string.Equals(d.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
            if (thisPc is not null)
            {
                return thisPc.Id;
            }
            // 一致が無ければ Mac 同一ロジック（アクティブ Computer → 先頭 Computer）。
            var device = computers.FirstOrDefault(d => d.IsActive) ?? computers.FirstOrDefault()
                ?? throw SpotifyException.NoDevice();
            return device.Id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpotifyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SpotifyException.ApiFailed(ex.Message);
        }
    }

    private static Dictionary<string, string> Bearer(string token) =>
        new() { ["Authorization"] = $"Bearer {token}" };

    private static Dictionary<string, string> JsonAuth(string token) =>
        new() { ["Authorization"] = $"Bearer {token}", ["Content-Type"] = "application/json" };

    // MARK: - JSON モデル

    private sealed record PlayBody([property: JsonPropertyName("uris")] IReadOnlyList<string> Uris);

    private sealed record TransferBody(
        [property: JsonPropertyName("device_ids")] IReadOnlyList<string> DeviceIds,
        [property: JsonPropertyName("play")] bool Play);

    private sealed class DevicesResponse
    {
        [JsonPropertyName("devices")] public List<Device>? Devices { get; set; }
    }

    private sealed class Device
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("is_active")] public bool IsActive { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class PlaybackResponse
    {
        [JsonPropertyName("is_playing")] public bool IsPlaying { get; set; }
        [JsonPropertyName("progress_ms")] public int? ProgressMs { get; set; }
        [JsonPropertyName("item")] public Item? Item { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("uri")] public string? Uri { get; set; }
        [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
    }
}
