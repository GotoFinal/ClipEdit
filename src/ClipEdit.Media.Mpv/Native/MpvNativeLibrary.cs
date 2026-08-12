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
        Command = GetDelegate<CommandDelegate>("mpv_command");
        WaitEvent = GetDelegate<WaitEventDelegate>("mpv_wait_event");
        ErrorString = GetDelegate<ErrorStringDelegate>("mpv_error_string");
        TerminateDestroy = GetDelegate<TerminateDestroyDelegate>("mpv_terminate_destroy");

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

    internal CommandDelegate Command { get; }

    internal WaitEventDelegate WaitEvent { get; }

    internal ErrorStringDelegate ErrorString { get; }

    internal TerminateDestroyDelegate TerminateDestroy { get; }

    public static MpvNativeLibrary Load(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        nint handle;
        try
        {
            handle = NativeLibrary.Load(Path.GetFullPath(libraryPath));
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
    internal delegate int CommandDelegate(nint handle, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint WaitEventDelegate(nint handle, double timeoutSeconds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint ErrorStringDelegate(int errorCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TerminateDestroyDelegate(nint handle);
}
