using System.Security.Cryptography;
using System.Text;

namespace AIRadio.Infrastructure;

/// <summary>OAuth PKCE（Proof Key for Code Exchange）のユーティリティ。</summary>
public static class Pkce
{
    /// <summary>code_verifier を生成する（base64url, 64 バイト → 86 文字）。</summary>
    public static string GenerateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    /// <summary>code_challenge = base64url(SHA256(code_verifier))。</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    /// <summary>base64url エンコード（+/= を URL 安全に）。</summary>
    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
