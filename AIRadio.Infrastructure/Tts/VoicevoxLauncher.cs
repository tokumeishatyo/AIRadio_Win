using System.Diagnostics;

namespace AIRadio.Infrastructure;

/// <summary>VOICEVOX 自動起動の結果（W-Win §3。診断ログ用）。</summary>
public enum VoicevoxLaunchResult
{
    /// <summary>既に起動済み（エンドポイントが応答）。</summary>
    AlreadyRunning,
    /// <summary>未起動だったので起動した。</summary>
    Launched,
    /// <summary>未起動だが exe パスが見つからず起動できない（BYO 手動）。</summary>
    NotConfigured,
    /// <summary>起動を試みたが失敗した。</summary>
    LaunchFailed,
}

/// <summary>
/// アプリ起動時に VOICEVOX が未応答なら起動する（W-Win §3）。完全 fail-tolerant（throw しない）。
/// probe は既存 <see cref="IHttpClient"/> シーム、起動は <see cref="Action{T}"/> デリゲート（実 1 個の interface を作らない＝§3-5）。
/// </summary>
public sealed class VoicevoxLauncher
{
    private readonly Uri _base;
    private readonly IHttpClient _http;
    private readonly string? _exePath;
    private readonly Action<string> _launch;

    /// <param name="exePath">解決済みの VOICEVOX.exe パス（null=未発見＝起動しない）。<see cref="ResolveExePath(string?)"/> で解決する。</param>
    /// <param name="launch">起動デリゲート（テスト差し替え用。既定は <see cref="Process.Start(ProcessStartInfo)"/>）。</param>
    public VoicevoxLauncher(string endpoint, IHttpClient http, string? exePath, Action<string>? launch = null)
    {
        _base = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : new Uri("http://127.0.0.1:50021/");
        _http = http;
        _exePath = exePath;
        _launch = launch ?? DefaultLaunch;
    }

    /// <summary>未応答なら起動する。throw しない（結果を呼び出し側がログ）。</summary>
    public async Task<VoicevoxLaunchResult> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (await IsReachableAsync(ct).ConfigureAwait(false))
        {
            return VoicevoxLaunchResult.AlreadyRunning;
        }
        if (string.IsNullOrWhiteSpace(_exePath))
        {
            return VoicevoxLaunchResult.NotConfigured;
        }
        try
        {
            _launch(_exePath);
            return VoicevoxLaunchResult.Launched;
        }
        catch (Exception)
        {
            return VoicevoxLaunchResult.LaunchFailed;
        }
    }

    /// <summary><c>GET {endpoint}version</c> で起動済みかを判定。接続不可のみ未起動扱い（不明は安全側で起動済み扱い）。</summary>
    private async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        try
        {
            await _http.GetAsync(new Uri(_base, "version"), headers: null, ct).ConfigureAwait(false);
            return true;            // 2xx 応答 = 起動済み。
        }
        catch (HttpStatusException)
        {
            return true;            // サーバが応答（非 2xx）= 起動はしている。
        }
        catch (HttpRequestException)
        {
            return false;           // 接続不可 = 未起動。
        }
        catch (Exception)
        {
            return true;            // 不明（キャンセル等）→ 起動済み扱い（spurious launch を避ける）。
        }
    }

    private static void DefaultLaunch(string exePath)
        => Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });

    /// <summary>tts.yaml 設定優先・未設定は標準インストール先を探索（W-Win §3）。</summary>
    public static string? ResolveExePath(string? configuredPath)
        => ResolveExePath(configuredPath, DefaultCandidates(), File.Exists);

    /// <summary>パス解決の純粋ロジック（候補・存在判定を注入＝テスト可）。設定優先（存在チェックせず）・未設定は最初に存在した候補。</summary>
    public static string? ResolveExePath(string? configuredPath, IReadOnlyList<string> candidates, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;   // 設定優先（誤設定は LaunchFailed で検知）。
        }
        foreach (var candidate in candidates)
        {
            if (exists(candidate))
            {
                return candidate;
            }
        }
        return null;   // 見つからない → BYO 手動。
    }

    private static IReadOnlyList<string> DefaultCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(localAppData, "Programs", "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(programFiles, "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(localAppData, "Programs", "voicevox", "VOICEVOX.exe"),
        };
    }
}
