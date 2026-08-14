using System.Runtime.InteropServices;
using ClipEdit.Media.Mpv.Native;

namespace ClipEdit.Media.Mpv;

public sealed class MpvOpenGlRenderContext : IDisposable
{
    private const int RenderParameterApiType = 1;
    private const int RenderParameterOpenGlInit = 2;
    private const int RenderParameterOpenGlFramebuffer = 3;
    private const int RenderParameterFlipY = 4;
    private const int RenderParameterDepth = 5;
    private const int GlRgba8 = 0x8058;
    private readonly MpvNativeLibrary _native;
    private readonly Func<string, nint> _getProcAddress;
    private readonly Action _requestRender;
    private readonly Action _released;
    private readonly GetProcAddressDelegate _getProcAddressCallback;
    private readonly UpdateDelegate _updateCallback;
    private readonly int _ownerThreadId;
    private nint _context;

    internal MpvOpenGlRenderContext(
        MpvNativeLibrary native,
        nint clientHandle,
        Func<string, nint> getProcAddress,
        Action requestRender,
        Action released)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        ArgumentNullException.ThrowIfNull(requestRender);

        _native = native;
        _getProcAddress = getProcAddress;
        _requestRender = requestRender;
        _released = released;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _getProcAddressCallback = ResolveOpenGlFunction;
        _updateCallback = OnUpdateRequested;

        using var apiType = new Utf8String("opengl");
        var initParameters = new MpvOpenGlInitParameters(
            Marshal.GetFunctionPointerForDelegate(_getProcAddressCallback),
            nint.Zero);

        var initPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<MpvOpenGlInitParameters>());
        try
        {
            Marshal.StructureToPtr(initParameters, initPointer, fDeleteOld: false);
            using var parameters = new NativeStructureArray<MpvRenderParameter>(
            [
                new MpvRenderParameter(RenderParameterApiType, apiType.Pointer),
                new MpvRenderParameter(RenderParameterOpenGlInit, initPointer),
                new MpvRenderParameter(0, nint.Zero),
            ]);
            Check(
                _native.RenderContextCreate(out _context, clientHandle, parameters.Pointer),
                "create the libmpv OpenGL render context");
        }
        catch
        {
            _released();
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(initPointer);
        }

        _native.RenderContextSetUpdateCallback(
            _context,
            Marshal.GetFunctionPointerForDelegate(_updateCallback),
            nint.Zero);
    }

    public void Render(int framebuffer, int width, int height, bool flipY = true)
    {
        VerifyOwnerThread();
        ObjectDisposedException.ThrowIf(_context == nint.Zero, this);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        _ = _native.RenderContextUpdate(_context);
        var target = new MpvOpenGlFramebuffer(framebuffer, width, height, GlRgba8);
        var flip = flipY ? 1 : 0;
        var depth = 8;
        var targetPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<MpvOpenGlFramebuffer>());
        var flipPointer = Marshal.AllocCoTaskMem(sizeof(int));
        var depthPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            Marshal.StructureToPtr(target, targetPointer, fDeleteOld: false);
            Marshal.WriteInt32(flipPointer, flip);
            Marshal.WriteInt32(depthPointer, depth);
            using var parameters = new NativeStructureArray<MpvRenderParameter>(
            [
                new MpvRenderParameter(RenderParameterOpenGlFramebuffer, targetPointer),
                new MpvRenderParameter(RenderParameterFlipY, flipPointer),
                new MpvRenderParameter(RenderParameterDepth, depthPointer),
                new MpvRenderParameter(0, nint.Zero),
            ]);
            Check(
                _native.RenderContextRender(_context, parameters.Pointer),
                "render a libmpv video frame");
        }
        finally
        {
            Marshal.FreeCoTaskMem(depthPointer);
            Marshal.FreeCoTaskMem(flipPointer);
            Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public void Dispose()
    {
        VerifyOwnerThread();
        if (_context == nint.Zero)
        {
            return;
        }

        _native.RenderContextSetUpdateCallback(_context, nint.Zero, nint.Zero);
        _native.RenderContextFree(_context);
        _context = nint.Zero;
        _released();
        GC.KeepAlive(_getProcAddressCallback);
        GC.KeepAlive(_updateCallback);
    }

    private nint ResolveOpenGlFunction(nint _, nint namePointer)
    {
        try
        {
            var name = Marshal.PtrToStringUTF8(namePointer);
            return name is null ? nint.Zero : _getProcAddress(name);
        }
        catch
        {
            return nint.Zero;
        }
    }

    private void OnUpdateRequested(nint _)
    {
        try
        {
            _requestRender();
        }
        catch
        {
            // Exceptions must never cross a native callback boundary.
        }
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The libmpv render context must be used and released on the OpenGL thread that created it.");
        }
    }

    private void Check(int errorCode, string operation)
    {
        if (errorCode < 0)
        {
            throw new MpvPreviewException($"Could not {operation}: {_native.DescribeError(errorCode)}.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetProcAddressDelegate(nint context, nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateDelegate(nint context);
}

file sealed class NativeStructureArray<T> : IDisposable
    where T : struct
{
    public NativeStructureArray(IReadOnlyList<T> values)
    {
        var itemSize = Marshal.SizeOf<T>();
        Pointer = Marshal.AllocCoTaskMem(checked(itemSize * values.Count));
        for (var index = 0; index < values.Count; index++)
        {
            Marshal.StructureToPtr(values[index], Pointer + (index * itemSize), fDeleteOld: false);
        }
    }

    public nint Pointer { get; }

    public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
}
