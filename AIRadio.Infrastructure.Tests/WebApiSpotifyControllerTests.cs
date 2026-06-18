using System.Text;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class WebApiSpotifyControllerTests
{
    private static readonly byte[] DevicesJson = Encoding.UTF8.GetBytes(
        "{\"devices\":[{\"id\":\"DEV\",\"is_active\":true,\"type\":\"Computer\",\"name\":\"My PC\"}]}");

    private static readonly byte[] PlaybackJson = Encoding.UTF8.GetBytes(
        "{\"is_playing\":true,\"progress_ms\":12500,\"item\":{\"uri\":\"spotify:track:abc\",\"duration_ms\":210000}}");

    private static readonly byte[] Empty = Array.Empty<byte>();

    private static (WebApiSpotifyController, FakeHttpClient) MakeController(Func<Uri, byte[]> responder)
    {
        var fake = new FakeHttpClient(responder);
        var controller = new WebApiSpotifyController(new FakeTokenProvider("TOK"), fake, retryDelaySeconds: 0);
        return (controller, fake);
    }

    private static FakeHttpClient.Request? PlayRequest(FakeHttpClient fake) =>
        fake.Requests.FirstOrDefault(r => r.Url.AbsoluteUri.Contains("/v1/me/player/play"));

    [Fact]
    public async Task Play_ResolvesDevice_AndSendsUris()
    {
        var (controller, fake) = MakeController(url => url.AbsoluteUri.Contains("/devices") ? DevicesJson : Empty);

        await controller.PlayAsync("spotify:track:abc");

        var playReq = PlayRequest(fake);
        Assert.NotNull(playReq);
        Assert.Equal("PUT", playReq!.Method);
        Assert.Contains("device_id=DEV", playReq.Url.Query);
        Assert.Equal("Bearer TOK", playReq.Headers!["Authorization"]);
        Assert.Contains("spotify:track:abc", Encoding.UTF8.GetString(playReq.Body!));
    }

    [Fact]
    public async Task Play_WithoutDevice_ThrowsNoDevice()
    {
        var (controller, _) = MakeController(url =>
            url.AbsoluteUri.Contains("/devices") ? Encoding.UTF8.GetBytes("{\"devices\":[]}") : Empty);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => controller.PlayAsync("spotify:track:abc"));
        Assert.Equal("E-SPT-NO-DEVICE-001", ex.Code);
    }

    // スマホがアクティブでも、この PC（Computer）を選ぶ。Connect 転送で別の場所で鳴らさない。
    [Fact]
    public async Task Play_PrefersComputer_OverActivePhone()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"devices\":[" +
            "{\"id\":\"PHONE\",\"is_active\":true,\"type\":\"Smartphone\",\"name\":\"iPhone\"}," +
            "{\"id\":\"PC\",\"is_active\":false,\"type\":\"Computer\",\"name\":\"My PC\"}]}");
        var (controller, fake) = MakeController(url => url.AbsoluteUri.Contains("/devices") ? json : Empty);

        await controller.PlayAsync("spotify:track:abc");

        Assert.Contains("device_id=PC", PlayRequest(fake)!.Url.Query);
    }

    // device_name 未指定でも、この PC（表示名＝コンピュータ名）の Computer を最優先で選ぶ（別 Computer がアクティブでも）。
    [Fact]
    public async Task Play_PrefersThisPcByHostname_OverOtherActiveComputer()
    {
        var host = Environment.MachineName;
        var json = Encoding.UTF8.GetBytes(
            "{\"devices\":[" +
            "{\"id\":\"OTHER\",\"is_active\":true,\"type\":\"Computer\",\"name\":\"OtherPC\"}," +
            "{\"id\":\"THISPC\",\"is_active\":false,\"type\":\"Computer\",\"name\":\"" + host + "\"}]}");
        var (controller, fake) = MakeController(url => url.AbsoluteUri.Contains("/devices") ? json : Empty);

        await controller.PlayAsync("spotify:track:abc");

        Assert.Contains("device_id=THISPC", PlayRequest(fake)!.Url.Query); // アクティブな別 Computer より自分を優先
    }

    // ホスト名に一致する Computer が無ければ、従来どおり（Mac 同一）アクティブな Computer → 先頭の Computer に退行する。
    [Fact]
    public async Task Play_FallsBackToActiveComputer_WhenNoHostnameMatch()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"devices\":[" +
            "{\"id\":\"IDLE\",\"is_active\":false,\"type\":\"Computer\",\"name\":\"Idle-PC-x9\"}," +
            "{\"id\":\"ACTIVE\",\"is_active\":true,\"type\":\"Computer\",\"name\":\"Active-PC-x9\"}]}");
        var (controller, fake) = MakeController(url => url.AbsoluteUri.Contains("/devices") ? json : Empty);

        await controller.PlayAsync("spotify:track:abc");

        Assert.Contains("device_id=ACTIVE", PlayRequest(fake)!.Url.Query);
    }

    [Fact]
    public async Task Play_WithoutComputerDevice_ThrowsNoDevice()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"devices\":[{\"id\":\"PHONE\",\"is_active\":true,\"type\":\"Smartphone\",\"name\":\"iPhone\"}]}");
        var (controller, _) = MakeController(url => url.AbsoluteUri.Contains("/devices") ? json : Empty);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => controller.PlayAsync("spotify:track:abc"));
        Assert.Equal("E-SPT-NO-DEVICE-001", ex.Code);
    }

    [Fact]
    public async Task Play_HonorsPreferredDeviceName()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"devices\":[" +
            "{\"id\":\"PC1\",\"is_active\":true,\"type\":\"Computer\",\"name\":\"Mini\"}," +
            "{\"id\":\"PC2\",\"is_active\":false,\"type\":\"Computer\",\"name\":\"Studio\"}]}");
        var fake = new FakeHttpClient(url => url.AbsoluteUri.Contains("/devices") ? json : Empty);
        var controller = new WebApiSpotifyController(
            new FakeTokenProvider("TOK"), fake, retryDelaySeconds: 0, preferredDeviceName: "Studio");

        await controller.PlayAsync("spotify:track:abc");

        Assert.Contains("device_id=PC2", PlayRequest(fake)!.Url.Query);
    }

    [Fact]
    public async Task Play_WithMissingPreferredDevice_ThrowsNoDevice()
    {
        var fake = new FakeHttpClient(url => url.AbsoluteUri.Contains("/devices") ? DevicesJson : Empty);
        var controller = new WebApiSpotifyController(
            new FakeTokenProvider("TOK"), fake, retryDelaySeconds: 0, preferredDeviceName: "ない子");

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => controller.PlayAsync("spotify:track:abc"));
        Assert.Equal("E-SPT-NO-DEVICE-001", ex.Code);
    }

    [Fact]
    public async Task SetVolume_SendsPercent()
    {
        var (controller, fake) = MakeController(_ => Empty);

        await controller.SetVolumeAsync(80);

        var req = fake.Requests.First(r => r.Url.AbsoluteUri.Contains("/v1/me/player/volume"));
        Assert.Equal("PUT", req.Method);
        Assert.Contains("volume_percent=80", req.Url.Query);
    }

    [Theory]
    [InlineData(150, "volume_percent=100")]
    [InlineData(-5, "volume_percent=0")]
    public async Task SetVolume_ClampsToRange(int input, string expectedQuery)
    {
        var (controller, fake) = MakeController(_ => Empty);

        await controller.SetVolumeAsync(input);

        var req = fake.Requests.First(r => r.Url.AbsoluteUri.Contains("/v1/me/player/volume"));
        Assert.Contains(expectedQuery, req.Url.Query);
    }

    [Fact]
    public async Task Seek_ConvertsSecondsToMillis()
    {
        var (controller, fake) = MakeController(_ => Empty);

        await controller.SeekAsync(30);

        var req = fake.Requests.First(r => r.Url.AbsoluteUri.Contains("/v1/me/player/seek"));
        Assert.Contains("position_ms=30000", req.Url.Query);
    }

    [Fact]
    public async Task Pause_SendsPut()
    {
        var (controller, fake) = MakeController(_ => Empty);

        await controller.PauseAsync();

        var req = fake.Requests.First(r => r.Url.AbsoluteUri.Contains("/v1/me/player/pause"));
        Assert.Equal("PUT", req.Method);
    }

    [Fact]
    public async Task Pause_SwallowsErrors()
    {
        // 既に停止済みなどで 403 でも、後始末の pause は例外を投げない。
        var (controller, _) = MakeController(_ => throw new HttpStatusException(403));

        await controller.PauseAsync(); // throw しなければ成功
    }

    [Fact]
    public async Task PlayerState_ParsesPlayback()
    {
        var (controller, _) = MakeController(_ => PlaybackJson);

        var state = await controller.PlayerStateAsync();

        // 曲長は URI・位置と同一スナップショットで返す（別問い合わせの stale 対策, S12 fix）。
        Assert.Equal(
            new PlayerState(PlaybackState.Playing, "spotify:track:abc", 12.5, 210.0), state);
    }

    [Fact]
    public async Task PlayerState_EmptyMeansStopped()
    {
        var (controller, _) = MakeController(_ => Empty); // 204 No Content

        var state = await controller.PlayerStateAsync();

        Assert.Equal(new PlayerState(PlaybackState.Stopped), state);
    }

    [Fact]
    public async Task CurrentTrackDuration_FromPlayback()
    {
        var (controller, _) = MakeController(_ => PlaybackJson);

        Assert.Equal(210.0, await controller.CurrentTrackDurationSecondsAsync());
    }

    [Fact]
    public async Task Play_RetriesAfterStaleDevice404_ViaTransfer()
    {
        // アイドルで stale なデバイスは device_id 指定でも 404 を返す。
        // transfer playback で起こして再試行し、2 回目で成功する。
        var playAttempts = 0;
        var fake = new FakeHttpClient(url =>
        {
            if (url.AbsoluteUri.Contains("/devices"))
            {
                return DevicesJson;
            }
            if (url.AbsoluteUri.Contains("/v1/me/player/play"))
            {
                if (++playAttempts == 1)
                {
                    throw new HttpStatusException(404);
                }
                return Empty;
            }
            return Empty; // transfer playback（PUT /v1/me/player）
        });
        var controller = new WebApiSpotifyController(new FakeTokenProvider("TOK"), fake, retryDelaySeconds: 0);

        await controller.PlayAsync("spotify:track:abc");

        var transfer = fake.Requests.FirstOrDefault(
            r => r.Url.AbsoluteUri.EndsWith("/v1/me/player") && r.Method == "PUT");
        Assert.NotNull(transfer);
        var body = Encoding.UTF8.GetString(transfer!.Body!);
        Assert.Contains("DEV", body);
        Assert.Contains("\"play\":false", body);
        Assert.Equal(2, fake.Requests.Count(r => r.Url.AbsoluteUri.Contains("/v1/me/player/play")));
    }

    [Fact]
    public async Task Play_GivesUpAfterPersistent404()
    {
        var fake = new FakeHttpClient(url =>
        {
            if (url.AbsoluteUri.Contains("/devices"))
            {
                return DevicesJson;
            }
            if (url.AbsoluteUri.Contains("/v1/me/player/play"))
            {
                throw new HttpStatusException(404);
            }
            return Empty;
        });
        var controller = new WebApiSpotifyController(new FakeTokenProvider("TOK"), fake, retryDelaySeconds: 0);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => controller.PlayAsync("spotify:track:abc"));
        Assert.Equal("E-SPT-API-FAILED-001", ex.Code);
        Assert.Equal(3, fake.Requests.Count(r => r.Url.AbsoluteUri.Contains("/v1/me/player/play")));
    }
}
