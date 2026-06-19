using System.Runtime.InteropServices;
using System.Text;

namespace AIRadio.Infrastructure;

/// <summary>
/// <see cref="INativeAquesTalk1"/> の本番実装（W-AQT）。AquesTalk1.0 / AqKanji2Koe の DLL を
/// <see cref="NativeLibrary"/> でフルパス動的ロードし、<c>__stdcall</c> デリゲート経由で呼ぶ。
/// 声種別の <c>AquesTalk.dll</c>（同名・フォルダ違い）を複数モジュールとして扱うため <see cref="DllImportAttribute"/> は使えない。
/// </summary>
/// <remarks>
/// 実 DLL を呼ぶため単体テストは付けず（<see cref="AquesTalk1Synthesizer"/> は fake で検証）、検証は実機で行う。
/// marshalling 定石は旧 Windows 版 <c>AppNativeAquesTalk</c> を流用。スレッド安全のため全 native 呼び出しを 1 つの lock で直列化する。
/// </remarks>
public sealed class AquesTalk1Native : INativeAquesTalk1
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AquesTalkSyntheUtf8(byte[] koe, int iSpeed, out int pSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void AquesTalkFreeWave(IntPtr wav);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AquesTalkSetKey([MarshalAs(UnmanagedType.LPStr)] string key);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AqKanji2KoeCreate([MarshalAs(UnmanagedType.LPStr)] string pathDic, out int pErr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void AqKanji2KoeRelease(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AqKanji2KoeConvertUtf8(IntPtr handle, byte[] kanji, byte[] koe, int nBufKoe);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AqKanji2KoeSetDevKey([MarshalAs(UnmanagedType.LPStr)] string devKey);

    private readonly record struct VoiceModule(IntPtr Module, AquesTalkSyntheUtf8 Synthe, AquesTalkFreeWave Free);

    private readonly object _lock = new();
    private readonly string _voicesDir;
    private readonly string _aqk2kDllDir;
    private readonly string? _aqtkDevKey;
    private readonly string? _aqtkUsrKey;
    private readonly Dictionary<string, VoiceModule> _voices = new(StringComparer.Ordinal);

    private IntPtr _aqk2kModule;
    private AqKanji2KoeRelease? _release;
    private AqKanji2KoeConvertUtf8? _convert;
    private bool _disposed;

    /// <param name="voicesDir">声種フォルダの親（この下に <c>&lt;voice&gt;/AquesTalk.dll</c>）。</param>
    /// <param name="aqk2kDllDir"><c>AqKanji2Koe.dll</c> を含むディレクトリ。</param>
    /// <param name="aqtkDevKey">AquesTalk1 開発キー（空なら未設定＝評価版でナ/マ行→ヌ）。</param>
    /// <param name="aqtkUsrKey">AquesTalk1 使用キー（空なら未設定＝音声透かしあり）。</param>
    public AquesTalk1Native(string voicesDir, string aqk2kDllDir, string? aqtkDevKey, string? aqtkUsrKey)
    {
        _voicesDir = voicesDir;
        _aqk2kDllDir = aqk2kDllDir;
        _aqtkDevKey = aqtkDevKey;
        _aqtkUsrKey = aqtkUsrKey;
    }

    public IntPtr CreateKanjiToKoe(string dictDir, string? aqk2kDevKey, out int errCode)
    {
        lock (_lock)
        {
            try
            {
                _aqk2kModule = NativeLibrary.Load(Path.Combine(_aqk2kDllDir, "AqKanji2Koe.dll"));
                var create = GetDelegate<AqKanji2KoeCreate>(_aqk2kModule, "AqKanji2Koe_Create");
                _release = GetDelegate<AqKanji2KoeRelease>(_aqk2kModule, "AqKanji2Koe_Release");
                _convert = GetDelegate<AqKanji2KoeConvertUtf8>(_aqk2kModule, "AqKanji2Koe_Convert_utf8");
                if (!string.IsNullOrEmpty(aqk2kDevKey))
                {
                    GetDelegate<AqKanji2KoeSetDevKey>(_aqk2kModule, "AqKanji2Koe_SetDevKey")(aqk2kDevKey);
                }
                // 失敗時は Zero + errCode（辞書不正＝INIT。Synthesizer ctor が解釈）。
                return create(dictDir, out errCode);
            }
            catch (Exception)
            {
                // DLL ロード／エクスポート解決の失敗 → LOAD（T03）。
                errCode = AquesTalk1Interop.LoadFailedErrorCode;
                return IntPtr.Zero;
            }
        }
    }

    public string ConvertKanjiToKoe(IntPtr handle, string japaneseText, out int errCode)
    {
        lock (_lock)
        {
            var input = AppendNullTerminator(Encoding.UTF8.GetBytes(japaneseText));
            var buffer = new byte[Math.Max(8192, input.Length * 8 + 256)];
            errCode = _convert!(handle, input, buffer, buffer.Length);
            if (errCode != 0)
            {
                return string.Empty;
            }
            var nullIdx = Array.IndexOf(buffer, (byte)0);
            var len = nullIdx >= 0 ? nullIdx : buffer.Length;
            return Encoding.UTF8.GetString(buffer, 0, len);
        }
    }

    public void ReleaseKanjiToKoe(IntPtr handle)
    {
        lock (_lock)
        {
            if (handle != IntPtr.Zero)
            {
                _release?.Invoke(handle);
            }
        }
    }

    public byte[] Synthesize(string voiceId, string koe, int speed, out int errCode)
    {
        lock (_lock)
        {
            if (!_voices.TryGetValue(voiceId, out var vm))
            {
                try
                {
                    var module = NativeLibrary.Load(Path.Combine(_voicesDir, voiceId, "AquesTalk.dll"));
                    var synthe = GetDelegate<AquesTalkSyntheUtf8>(module, "AquesTalk_Synthe_Utf8");
                    var free = GetDelegate<AquesTalkFreeWave>(module, "AquesTalk_FreeWave");
                    // 各声種モジュールにキー設定（合成前に一度）。非空時のみ。
                    if (!string.IsNullOrEmpty(_aqtkDevKey))
                    {
                        GetDelegate<AquesTalkSetKey>(module, "AquesTalk_SetDevKey")(_aqtkDevKey);
                    }
                    if (!string.IsNullOrEmpty(_aqtkUsrKey))
                    {
                        GetDelegate<AquesTalkSetKey>(module, "AquesTalk_SetUsrKey")(_aqtkUsrKey);
                    }
                    vm = new VoiceModule(module, synthe, free);
                    _voices[voiceId] = vm;
                }
                catch (Exception)
                {
                    errCode = AquesTalk1Interop.LoadFailedErrorCode;   // 声種DLLロード失敗（T03）
                    return Array.Empty<byte>();
                }
            }

            var koeBytes = AppendNullTerminator(Encoding.UTF8.GetBytes(koe));
            var wavPtr = vm.Synthe(koeBytes, speed, out var size);
            if (wavPtr == IntPtr.Zero)
            {
                errCode = size;   // AquesTalk は失敗時 NULL + pSize にエラーコード
                return Array.Empty<byte>();
            }
            try
            {
                errCode = 0;
                var wav = new byte[size];
                Marshal.Copy(wavPtr, wav, 0, size);
                return wav;
            }
            finally
            {
                vm.Free(wavPtr);   // メモリリーク防止: 必ず FreeWave を呼ぶ
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var vm in _voices.Values)
            {
                if (vm.Module != IntPtr.Zero)
                {
                    NativeLibrary.Free(vm.Module);
                }
            }
            _voices.Clear();
            if (_aqk2kModule != IntPtr.Zero)
            {
                NativeLibrary.Free(_aqk2kModule);
                _aqk2kModule = IntPtr.Zero;
            }
        }
    }

    private static T GetDelegate<T>(IntPtr module, string export) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, export));

    /// <summary>byte[] 末尾に NULL 終端を足す（C 文字列前提）。</summary>
    private static byte[] AppendNullTerminator(byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        result[bytes.Length] = 0;
        return result;
    }
}
