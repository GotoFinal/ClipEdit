using System.Runtime.InteropServices;

namespace ClipEdit.Media.Mpv.Native;

internal sealed class Utf8String : IDisposable
{
    public Utf8String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Pointer = Marshal.StringToCoTaskMemUTF8(value);
    }

    public nint Pointer { get; }

    public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
}
