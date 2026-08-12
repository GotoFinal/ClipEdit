using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Mpv.Native;

internal sealed class MpvClient : IDisposable
{
    private const int FileLoadedEvent = 8;
    private const int EndFileEvent = 7;
    private readonly MpvNativeLibrary _native;
    private nint _handle;

    public MpvClient(MpvNativeLibrary native)
    {
        _native = native;
        _handle = native.Create();
        if (_handle == nint.Zero)
        {
            throw new MpvPreviewException("libmpv could not create a client handle.");
        }

        try
        {
            SetOption("config", "no");
            SetOption("terminal", "no");
            SetOption("input-default-bindings", "no");
            SetOption("input-vo-keyboard", "no");
            SetOption("keep-open", "yes");
            SetOption("pause", "yes");
            SetOption("vo", "libmpv");
            Check(_native.Initialize(_handle), "initialize libmpv");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public nint Handle => _handle;

    public void Load(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Preview source media was not found.", fullPath);
        }

        RunCommand("loadfile", fullPath, "replace");
        WaitUntilLoaded(cancellationToken);
    }

    public void Seek(MediaTime position)
    {
        if (position < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Preview position cannot be negative.");
        }

        RunCommand(
            "seek",
            position.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            "absolute+exact");
    }

    public void SetPaused(bool isPaused) => SetProperty("pause", isPaused ? "yes" : "no");

    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Preview volume must be between 0 and 1.");
        }

        SetProperty("volume", (volume * 100).ToString("R", CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        _native.TerminateDestroy(_handle);
        _handle = nint.Zero;
    }

    private void WaitUntilLoaded(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventPointer = _native.WaitEvent(_handle, 0.1);
            var eventData = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (eventData.EventId == FileLoadedEvent)
            {
                return;
            }

            if (eventData.EventId == EndFileEvent && eventData.Data != nint.Zero)
            {
                var endFile = Marshal.PtrToStructure<MpvEndFileEvent>(eventData.Data);
                if (endFile.Error < 0)
                {
                    Check(endFile.Error, "load preview media");
                }
            }
        }

        throw new TimeoutException("libmpv did not load the preview media within 30 seconds.");
    }

    private void SetOption(string name, string value)
    {
        using var nativeName = new Utf8String(name);
        using var nativeValue = new Utf8String(value);
        Check(_native.SetOptionString(_handle, nativeName.Pointer, nativeValue.Pointer), $"set option '{name}'");
    }

    private void SetProperty(string name, string value)
    {
        using var nativeName = new Utf8String(name);
        using var nativeValue = new Utf8String(value);
        Check(_native.SetPropertyString(_handle, nativeName.Pointer, nativeValue.Pointer), $"set property '{name}'");
    }

    private void RunCommand(params string[] arguments)
    {
        var strings = arguments.Select(argument => new Utf8String(argument)).ToArray();
        var arrayPointer = Marshal.AllocCoTaskMem((strings.Length + 1) * nint.Size);
        try
        {
            for (var index = 0; index < strings.Length; index++)
            {
                Marshal.WriteIntPtr(arrayPointer, index * nint.Size, strings[index].Pointer);
            }

            Marshal.WriteIntPtr(arrayPointer, strings.Length * nint.Size, nint.Zero);
            Check(_native.Command(_handle, arrayPointer), $"run command '{arguments[0]}'");
        }
        finally
        {
            Marshal.FreeCoTaskMem(arrayPointer);
            foreach (var value in strings)
            {
                value.Dispose();
            }
        }
    }

    private void Check(int errorCode, string operation)
    {
        if (errorCode < 0)
        {
            throw new MpvPreviewException($"Could not {operation}: {_native.DescribeError(errorCode)}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEvent
    {
        public readonly int EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEndFileEvent
    {
        public readonly int Reason;
        public readonly int Error;
    }
}
