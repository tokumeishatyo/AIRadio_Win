using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

/// <summary>設定可能な <see cref="INativeAquesTalk1"/> fake。呼び出し順と引数を記録する（W-AQT テスト共通）。</summary>
internal sealed class FakeNativeAquesTalk1 : INativeAquesTalk1
{
    public int CreateErrCode;
    public int ConvertErrCode;
    public int SynthErrCode;
    public string ConvertResult = "コエ";
    public byte[] SynthResult = new byte[64];

    public List<string> Calls { get; } = new();
    public string? LastVoiceId { get; private set; }
    public string? LastKoe { get; private set; }
    public int LastSpeed { get; private set; }
    public bool Released { get; private set; }
    public bool Disposed { get; private set; }

    public IntPtr CreateKanjiToKoe(string dictDir, string? aqk2kDevKey, out int errCode)
    {
        Calls.Add("create");
        errCode = CreateErrCode;
        return CreateErrCode == 0 ? (IntPtr)1 : IntPtr.Zero;
    }

    public string ConvertKanjiToKoe(IntPtr handle, string japaneseText, out int errCode)
    {
        Calls.Add("convert");
        errCode = ConvertErrCode;
        return ConvertErrCode == 0 ? ConvertResult : string.Empty;
    }

    public void ReleaseKanjiToKoe(IntPtr handle)
    {
        Released = true;
        Calls.Add("release");
    }

    public byte[] Synthesize(string voiceId, string koe, int speed, out int errCode)
    {
        Calls.Add("synth");
        LastVoiceId = voiceId;
        LastKoe = koe;
        LastSpeed = speed;
        errCode = SynthErrCode;
        return SynthErrCode == 0 ? SynthResult : Array.Empty<byte>();
    }

    public void Dispose() => Disposed = true;
}
