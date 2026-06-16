using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// 番組の長さ（メニュー選択）の永続化（仕様 w13 §5）。Mac の <c>UserDefaults</c> 相当を Windows では
/// <c>%LOCALAPPDATA%\AIRadio\program-length</c> の平文ファイル（<see cref="ProgramLength.RawValue"/>）で保持する
/// （<see cref="DpapiTokenStore"/> と同じディレクトリ。番組長は機密でないため暗号化しない）。
/// <para>新 interface は作らない（§3-5。差し替え境界ではない）。解析ロジックは Core の
/// <see cref="ProgramLength.TryParse"/> に集約し、本クラスはファイル IO のみ。</para>
/// </summary>
public sealed class ProgramLengthStore
{
    private readonly string _path;

    public ProgramLengthStore(string? path = null)
        => _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIRadio", "program-length");

    /// <summary>保存された番組長。ファイル不在・解析不能は null（呼び出し側で既定へ倒す）。</summary>
    public ProgramLength? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            var raw = File.ReadAllText(_path).Trim();
            return ProgramLength.TryParse(raw, out var length) ? length : null;
        }
        catch
        {
            return null; // 読み取り失敗は「未設定」と同じ扱い（fail-tolerant）。
        }
    }

    public void Write(ProgramLength length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, length.RawValue);
    }
}
