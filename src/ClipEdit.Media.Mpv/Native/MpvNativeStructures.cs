using System.Runtime.InteropServices;

namespace ClipEdit.Media.Mpv.Native;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvRenderParameter(int type, nint data)
{
    public readonly int Type = type;
    public readonly nint Data = data;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvOpenGlInitParameters(nint getProcAddress, nint context)
{
    public readonly nint GetProcAddress = getProcAddress;
    public readonly nint Context = context;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvOpenGlFramebuffer(int framebuffer, int width, int height, int internalFormat)
{
    public readonly int Framebuffer = framebuffer;
    public readonly int Width = width;
    public readonly int Height = height;
    public readonly int InternalFormat = internalFormat;
}
