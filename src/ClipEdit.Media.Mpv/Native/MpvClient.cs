using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Native;

internal sealed class MpvClient : IDisposable
{
    private const int FileLoadedEvent = 8;
    private const int EndFileEvent = 7;
    private const int PropertyUnavailableError = -10;
    private const int DoubleFormat = 5;
    private const int Int64Format = 4;
    private const int FlagFormat = 3;
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
            SetOption("hwdec", "auto");
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

    public MediaTime? GetPosition()
    {
        using var nativeName = new Utf8String("time-pos");
        var valuePointer = Marshal.AllocCoTaskMem(sizeof(double));
        try
        {
            var result = _native.GetProperty(_handle, nativeName.Pointer, DoubleFormat, valuePointer);
            if (result == PropertyUnavailableError)
            {
                return null;
            }

            Check(result, "read preview position");
            var seconds = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(valuePointer));
            if (!double.IsFinite(seconds) || seconds < 0)
            {
                return null;
            }

            var microseconds = Math.Round(seconds * 1_000_000, MidpointRounding.AwayFromZero);
            if (microseconds > long.MaxValue)
            {
                return null;
            }

            return new MediaTime((long)microseconds, 1_000_000);
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    public PreviewPlaybackSnapshot GetPlaybackSnapshot()
    {
        return new PreviewPlaybackSnapshot(
            GetPosition(),
            GetFlagProperty("eof-reached"),
            GetStringProperty("hwdec-current"));
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

    public void SetAudioTracks(IReadOnlyList<PreviewAudioTrack> audioTracks)
    {
        ArgumentNullException.ThrowIfNull(audioTracks);
        if (audioTracks.Any(track => track is null) ||
            audioTracks.Select(track => track.StreamIndex).Distinct().Count() != audioTracks.Count)
        {
            throw new ArgumentException(
                "Preview audio tracks must be non-null and use distinct stream indices.",
                nameof(audioTracks));
        }

        var enabledTracks = audioTracks.Where(track => !track.IsMuted).ToArray();
        if (enabledTracks.Length == 0)
        {
            SetProperty("lavfi-complex", string.Empty);
            SetProperty("aid", "no");
            return;
        }

        var availableTracks = GetAudioTrackIdsByFfmpegIndex();
        var graphTracks = enabledTracks.Select(track =>
        {
            if (!availableTracks.TryGetValue(track.StreamIndex, out var mpvTrackId))
            {
                throw new MpvPreviewException(
                    $"libmpv did not expose embedded audio stream {track.StreamIndex}.");
            }

            return new MpvAudioGraphTrack(mpvTrackId, track.GainDb);
        }).ToArray();

        SetProperty("lavfi-complex", MpvAudioGraphBuilder.Build(graphTracks));
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

    private IReadOnlyDictionary<int, long> GetAudioTrackIdsByFfmpegIndex()
    {
        var count = GetInt64Property("track-list/count");
        if (count is < 0 or > 10_000)
        {
            throw new MpvPreviewException($"libmpv reported an invalid track count: {count}.");
        }

        var tracks = new Dictionary<int, long>();
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(
                    GetStringProperty($"track-list/{index}/type"),
                    "audio",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var ffmpegIndex = GetInt64Property($"track-list/{index}/ff-index");
            var mpvTrackId = GetInt64Property($"track-list/{index}/id");
            if (ffmpegIndex is < 0 or > int.MaxValue || mpvTrackId <= 0)
            {
                continue;
            }

            tracks[checked((int)ffmpegIndex)] = mpvTrackId;
        }

        return tracks;
    }

    private long GetInt64Property(string name)
    {
        using var nativeName = new Utf8String(name);
        var valuePointer = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            Check(_native.GetProperty(_handle, nativeName.Pointer, Int64Format, valuePointer), $"read '{name}'");
            return Marshal.ReadInt64(valuePointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    private bool GetFlagProperty(string name)
    {
        using var nativeName = new Utf8String(name);
        var valuePointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            var result = _native.GetProperty(_handle, nativeName.Pointer, FlagFormat, valuePointer);
            if (result == PropertyUnavailableError)
            {
                return false;
            }

            Check(result, $"read '{name}'");
            return Marshal.ReadInt32(valuePointer) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    private string? GetStringProperty(string name)
    {
        using var nativeName = new Utf8String(name);
        var valuePointer = _native.GetPropertyString(_handle, nativeName.Pointer);
        if (valuePointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(valuePointer);
        }
        finally
        {
            _native.Free(valuePointer);
        }
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
