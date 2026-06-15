using System.Security.Cryptography;
using System.Text;

namespace AIRadio.Infrastructure;

/// <summary>文字列トークンの保管抽象（テストで fake 差し替え可能にする）。</summary>
public interface ITokenStore
{
    void Save(string value);
    string? Load();
    void Delete();
}

/// <summary>
/// DPAPI（<see cref="ProtectedData"/>, CurrentUser）で暗号化し refresh トークンを保管する実装。
/// 既定の配置は %LOCALAPPDATA%\AIRadio\spotify.token。
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _path;

    public DpapiTokenStore(string? path = null)
        => _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIRadio", "spotify.token");

    public void Save(string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, encrypted);
    }

    public string? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(_path), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null; // 破損・別ユーザー暗号化など
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
