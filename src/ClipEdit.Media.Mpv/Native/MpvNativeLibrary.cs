using System.Runtime.InteropServices;

namespace ClipEdit.Media.Mpv.Native;

internal sealed class MpvNativeLibrary : IDisposable
{
    private const int ExpectedApiMajor = 2;
    private const int MinimumApiMinor = 5;

    private readonly nint _libraryHandle;
    private bool _disposed;

    private MpvNativeLibrary(nint libraryHandle)
    {
        _libraryHandle = libraryHandle;
        ClientApiVersion = GetDelegate<ClientApiVersionDelegate>("mpv_client_api_version");
        Create = GetDelegate<CreateDelegate>("mpv_create");
        Initialize = GetDelegate<InitializeDelegate>("mpv_initialize");
        SetOptionString = GetDelegate<SetStringDelegate>("mpv_set_option_string");
        SetPropertyString = GetDelegate<SetStringDelegate>("mpv_set_property_string");
        SetPropertyAsync = GetDelegate<SetPropertyAsyncDelegate>("mpv_set_property_async");
        GetProperty = GetDelegate<GetPropertyDelegate>("mpv_get_property");
        GetPropertyString = GetDelegate<GetPropertyStringDelegate>("mpv_get_property_string");
        Free = GetDelegate<FreeDelegate>("mpv_free");
        Command = GetDelegate<CommandDelegate>("mpv_command");
        WaitEvent = GetDelegate<WaitEventDelegate>("mpv_wait_event");
        ErrorString = GetDelegate<ErrorStringDelegate>("mpv_error_string");
        TerminateDestroy = GetDelegate<TerminateDestroyDelegate>("mpv_terminate_destroy");
        RenderContextCreate = GetDelegate<RenderContextCreateDelegate>("mpv_render_context_create");
        RenderContextSetUpdateCallback = GetDelegate<RenderContextSetUpdateCallbackDelegate>(
            "mpv_render_context_set_update_callback");
        RenderContextUpdate = GetDelegate<RenderContextUpdateDelegate>("mpv_render_context_update");
        RenderContextRender = GetDelegate<RenderContextRenderDelegate>("mpv_render_context_render");
        RenderContextFree = GetDelegate<RenderContextFreeDelegate>("mpv_render_context_free");

        ApiVersion = MpvApiVersion.FromPacked(ClientApiVersion());
        if (ApiVersion.Major != ExpectedApiMajor || ApiVersion.Minor < MinimumApiMinor)
        {
            throw new MpvPreviewException(
                $"Unsupported libmpv client API {ApiVersion}; expected {ExpectedApiMajor}.{MinimumApiMinor} or a newer compatible minor version.");
        }
    }

    public MpvApiVersion ApiVersion { get; }

    internal ClientApiVersionDelegate ClientApiVersion { get; }

    internal CreateDelegate Create { get; }

    internal InitializeDelegate Initialize { get; }

    internal SetStringDelegate SetOptionString { get; }

    internal SetStringDelegate SetPropertyString { get; }

    internal SetPropertyAsyncDelegate SetPropertyAsync { get; }

    internal GetPropertyDelegate GetProperty { get; }

    internal GetPropertyStringDelegate GetPropertyString { get; }

    internal FreeDelegate Free { get; }

    internal CommandDelegate Command { get; }

    internal WaitEventDelegate WaitEvent { get; }

    internal ErrorStringDelegate ErrorString { get; }

    internal TerminateDestroyDelegate TerminateDestroy { get; }

    internal RenderContextCreateDelegate RenderContextCreate { get; }

    internal RenderContextSetUpdateCallbackDelegate RenderContextSetUpdateCallback { get; }

    internal RenderContextUpdateDelegate RenderContextUpdate { get; }

    internal RenderContextRenderDelegate RenderContextRender { get; }

    internal RenderContextFreeDelegate RenderContextFree { get; }

    public static MpvNativeLibrary Load(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        nint handle;
        try
        {
            var loadTarget = Path.IsPathFullyQualified(libraryPath)
                ? Path.GetFullPath(libraryPath)
                : libraryPath;
            handle = NativeLibrary.Load(loadTarget);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or FileNotFoundException)
        {
            throw new MpvPreviewException($"Could not load libmpv from '{libraryPath}'.", exception);
        }

        try
        {
            return new MpvNativeLibrary(handle);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeLibrary.Free(_libraryHandle);
        _disposed = true;
    }

    internal string DescribeError(int errorCode)
    {
        var pointer = ErrorString(errorCode);
        return pointer == nint.Zero
            ? $"libmpv error {errorCode}"
            : Marshal.PtrToStringUTF8(pointer) ?? $"libmpv error {errorCode}";
    }

    private TDelegate GetDelegate<TDelegate>(string exportName)
        where TDelegate : Delegate
    {
        var address = NativeLibrary.GetExport(_libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint ClientApiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint CreateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InitializeDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetStringDelegate(nint handle, nint name, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetPropertyAsyncDelegate(
        nint handle,
        ulong replyUserData,
        nint name,
        int format,
        nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetPropertyDelegate(nint handle, nint name, int format, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetPropertyStringDelegate(nint handle, nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FreeDelegate(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CommandDelegate(nint handle, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint WaitEventDelegate(nint handle, double timeoutSeconds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint ErrorStringDelegate(int errorCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TerminateDestroyDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RenderContextCreateDelegate(out nint context, nint handle, nint parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RenderContextSetUpdateCallbackDelegate(nint context, nint callback, nint callbackContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate ulong RenderContextUpdateDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RenderContextRenderDelegate(nint context, nint parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RenderContextFreeDelegate(nint context);
}
