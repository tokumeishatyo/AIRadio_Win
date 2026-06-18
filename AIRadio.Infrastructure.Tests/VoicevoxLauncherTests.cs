using System.Text;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>W-Win VOICEVOX 自動起動（probe → 未応答なら起動。完全 fail-tolerant）。</summary>
public class VoicevoxLauncherTests
{
    private const string Endpoint = "http://127.0.0.1:50021/";

    private static byte[] VersionResponse() => Encoding.UTF8.GetBytes("\"0.25.2\"");

    [Fact]
    public async Task EnsureRunning_Reachable_DoesNotLaunch_ProbesVersion()
    {
        var launched = new List<string>();
        var http = new FakeHttpClient(_ => VersionResponse());
        var launcher = new VoicevoxLauncher(Endpoint, http, exePath: "C:/voicevox.exe", launch: launched.Add);

        var result = await launcher.EnsureRunningAsync();

        Assert.Equal(VoicevoxLaunchResult.AlreadyRunning, result);
        Assert.Empty(launched);
        Assert.Contains(http.Requests, r => r.Method == "GET" && r.Url.AbsolutePath.EndsWith("/version"));
    }

    [Fact]
    public async Task EnsureRunning_NotReachable_WithExe_Launches()
    {
        var launched = new List<string>();
        var http = new FakeHttpClient(_ => throw new HttpRequestException("connection refused"));
        var launcher = new VoicevoxLauncher(Endpoint, http, exePath: "C:/voicevox.exe", launch: launched.Add);

        var result = await launcher.EnsureRunningAsync();

        Assert.Equal(VoicevoxLaunchResult.Launched, result);
        Assert.Equal("C:/voicevox.exe", Assert.Single(launched));
    }

    [Fact]
    public async Task EnsureRunning_NotReachable_NoExe_NotConfigured()
    {
        var launched = new List<string>();
        var http = new FakeHttpClient(_ => throw new HttpRequestException("connection refused"));
        var launcher = new VoicevoxLauncher(Endpoint, http, exePath: null, launch: launched.Add);

        var result = await launcher.EnsureRunningAsync();

        Assert.Equal(VoicevoxLaunchResult.NotConfigured, result);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task EnsureRunning_LaunchThrows_LaunchFailed_NoThrow()
    {
        var http = new FakeHttpClient(_ => throw new HttpRequestException("connection refused"));
        var launcher = new VoicevoxLauncher(
            Endpoint, http, exePath: "C:/bad.exe", launch: _ => throw new InvalidOperationException("boom"));

        var result = await launcher.EnsureRunningAsync();

        Assert.Equal(VoicevoxLaunchResult.LaunchFailed, result);
    }

    [Fact]
    public async Task EnsureRunning_ServerError_TreatedAsRunning_NoLaunch()
    {
        var launched = new List<string>();
        var http = new FakeHttpClient(_ => throw new HttpStatusException(500));
        var launcher = new VoicevoxLauncher(Endpoint, http, exePath: "C:/voicevox.exe", launch: launched.Add);

        var result = await launcher.EnsureRunningAsync();

        Assert.Equal(VoicevoxLaunchResult.AlreadyRunning, result);   // サーバが応答 = 起動済み
        Assert.Empty(launched);
    }

    [Fact]
    public void ResolveExePath_Configured_ReturnedWithoutExistenceCheck()
    {
        var resolved = VoicevoxLauncher.ResolveExePath(
            "C:/custom/VOICEVOX.exe", new[] { "C:/a.exe" }, _ => false);

        Assert.Equal("C:/custom/VOICEVOX.exe", resolved);
    }

    [Fact]
    public void ResolveExePath_Unconfigured_ReturnsFirstExistingCandidate()
    {
        var resolved = VoicevoxLauncher.ResolveExePath(
            null, new[] { "C:/a.exe", "C:/b.exe" }, p => p == "C:/b.exe");

        Assert.Equal("C:/b.exe", resolved);
    }

    [Fact]
    public void ResolveExePath_Unconfigured_NoneExist_ReturnsNull()
    {
        var resolved = VoicevoxLauncher.ResolveExePath(null, new[] { "C:/a.exe" }, _ => false);

        Assert.Null(resolved);
    }
}
