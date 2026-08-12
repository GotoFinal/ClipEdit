using System.Collections.Immutable;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed class AudioTrackViewModel : ViewModelBase, IDisposable
{
    private const double MaximumTimelineOffsetSeconds = 7 * 24 * 60 * 60;
    private readonly MediaTime _timelineQuantum;
    private SourceEdit _edit;
    private MediaTime _playhead;
    private MediaTime _selectionStart;
    private MediaTime _selectionEnd;
    private double _gainDb;
    private bool _isMuted;
    private MediaTime _timelineOffset;
    private double _timelineZoom = 1;
    private double _timelineViewportStart;
    private TimelineBitmapVisual? _waveform;
    private bool _isWaveformLoading;
    private string? _waveformErrorText;

    public AudioTrackViewModel(ImportedMedia media, AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(stream);
        var duration = stream.Duration ?? media.Probe.Duration;
        if (duration is null || duration <= MediaTime.Zero)
        {
            throw new ArgumentException("An audio track needs a known positive duration.", nameof(stream));
        }

        SourcePath = media.Probe.SourcePath;
        StreamIndex = stream.Index;
        IsExternal = media.IsExternalAudio;
        DisplayName = BuildDisplayName(media, stream);
        _timelineQuantum = stream.TimeBase is { } timeBase && timeBase > MediaTime.Zero
            ? timeBase
            : new MediaTime(1, stream.SampleRate ?? 48_000);
        _edit = new SourceEdit(duration.Value);
        _selectionEnd = duration.Value;
    }

    public string SourcePath { get; }

    public int StreamIndex { get; }

    public bool IsExternal { get; }

    public string DisplayName { get; }

    public string StableId => $"{SourcePath}|{StreamIndex}";

    public SourceEdit Edit
    {
        get => _edit;
        private set
        {
            if (SetProperty(ref _edit, value))
            {
                OnPropertyChanged(nameof(KeptRanges));
                OnPropertyChanged(nameof(IsEdited));
                OnPropertyChanged(nameof(HasRangeEdits));
                OnPropertyChanged(nameof(CanRemoveSelection));
                OnPropertyChanged(nameof(OutputDurationText));
            }
        }
    }

    public ImmutableArray<MediaRange> KeptRanges => Edit.KeptRanges;

    public bool IsEdited =>
        !Edit.IsUnedited || IsMuted || GainDb != 0 || TimelineOffset != MediaTime.Zero;

    public bool HasRangeEdits => !Edit.IsUnedited;

    public double DurationSeconds => Edit.SourceDuration.TotalSeconds;

    public double FrameStepSeconds => _timelineQuantum.TotalSeconds;

    public double PlayheadSeconds
    {
        get => _playhead.TotalSeconds;
        set
        {
            if (SetProperty(ref _playhead, Quantize(value)))
            {
                OnPropertyChanged(nameof(PlayheadText));
            }
        }
    }

    public string PlayheadText => FormatTimestamp(_playhead);

    public double SelectionStartSeconds
    {
        get => _selectionStart.TotalSeconds;
        set
        {
            if (SetProperty(ref _selectionStart, Quantize(value)))
            {
                RaiseSelectionChanged();
            }
        }
    }

    public double SelectionEndSeconds
    {
        get => _selectionEnd.TotalSeconds;
        set
        {
            if (SetProperty(ref _selectionEnd, Quantize(value)))
            {
                RaiseSelectionChanged();
            }
        }
    }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (SetProperty(ref _gainDb, Math.Clamp(value, -60, 12)))
            {
                OnPropertyChanged(nameof(GainText));
                OnPropertyChanged(nameof(IsEdited));
            }
        }
    }

    public string GainText => GainDb <= -59.95 ? "−∞ dB" : $"{GainDb:+0.0;-0.0;0.0} dB";

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(IsEdited));
            }
        }
    }

    public MediaTime TimelineOffset
    {
        get => _timelineOffset;
        private set
        {
            if (SetProperty(ref _timelineOffset, value))
            {
                OnPropertyChanged(nameof(TimelineOffsetSeconds));
                OnPropertyChanged(nameof(TimelineOffsetText));
                OnPropertyChanged(nameof(IsEdited));
            }
        }
    }

    public double TimelineOffsetSeconds
    {
        get => TimelineOffset.TotalSeconds;
        set
        {
            if (IsExternal)
            {
                TimelineOffset = QuantizeOffset(value);
            }
        }
    }

    public string TimelineOffsetText => $"Starts {FormatTimestamp(TimelineOffset)}";

    public bool CanRemoveSelection =>
        _selectionStart < _selectionEnd &&
        Edit.KeptRanges.Any(range => _selectionStart < range.End && _selectionEnd > range.Start);

    public string OutputDurationText => $"Audible {FormatTimestamp(Edit.OutputDuration)}";

    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            var zoom = TimelineViewportMath.ClampZoom(value);
            if (!SetProperty(ref _timelineZoom, zoom))
            {
                return;
            }

            TimelineViewportStart = _timelineViewportStart;
            OnPropertyChanged(nameof(TimelineViewportDurationSeconds));
            OnPropertyChanged(nameof(TimelineViewportEndSeconds));
            OnPropertyChanged(nameof(TimelineZoomText));
            OnPropertyChanged(nameof(TimelineViewportText));
            OnPropertyChanged(nameof(CanZoomTimelineIn));
            OnPropertyChanged(nameof(CanZoomTimelineOut));
        }
    }

    public double TimelineViewportStart
    {
        get => _timelineViewportStart;
        set
        {
            var start = TimelineViewportMath.ClampStart(DurationSeconds, TimelineZoom, value);
            if (SetProperty(ref _timelineViewportStart, start))
            {
                OnPropertyChanged(nameof(TimelineViewportEndSeconds));
                OnPropertyChanged(nameof(TimelineViewportText));
            }
        }
    }

    public double TimelineViewportDurationSeconds =>
        TimelineViewportMath.VisibleDuration(DurationSeconds, TimelineZoom);

    public double TimelineViewportEndSeconds =>
        Math.Min(DurationSeconds, TimelineViewportStart + TimelineViewportDurationSeconds);

    public string TimelineZoomText => $"{TimelineZoom:0.#}×";

    public string TimelineViewportText =>
        $"{FormatTimestamp(SecondsToDisplayTime(TimelineViewportStart))} – " +
        $"{FormatTimestamp(SecondsToDisplayTime(TimelineViewportEndSeconds))}";

    public bool CanZoomTimelineIn => TimelineZoom < TimelineViewportMath.MaximumZoom;

    public bool CanZoomTimelineOut => TimelineZoom > 1;

    public TimelineBitmapVisual? Waveform
    {
        get => _waveform;
        private set
        {
            var previous = _waveform;
            if (SetProperty(ref _waveform, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasWaveform));
            }
        }
    }

    public bool HasWaveform => Waveform is not null;

    public bool IsWaveformLoading
    {
        get => _isWaveformLoading;
        internal set => SetProperty(ref _isWaveformLoading, value);
    }

    public string? WaveformErrorText
    {
        get => _waveformErrorText;
        internal set
        {
            if (SetProperty(ref _waveformErrorText, value))
            {
                OnPropertyChanged(nameof(HasWaveformError));
            }
        }
    }

    public bool HasWaveformError => !string.IsNullOrWhiteSpace(WaveformErrorText);

    public bool RemoveSelection()
    {
        if (!CanRemoveSelection)
        {
            return false;
        }

        Edit = Edit.Remove(new MediaRange(_selectionStart, _selectionEnd));
        return true;
    }

    public void Reset()
    {
        Edit = Edit.Reset();
        _selectionStart = MediaTime.Zero;
        _selectionEnd = Edit.SourceDuration;
        GainDb = 0;
        IsMuted = false;
        TimelineOffset = MediaTime.Zero;
        RaiseSelectionChanged();
    }

    public void ZoomTimeline(double factor, double? anchorSeconds = null)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        var anchor = anchorSeconds ??
                     (PlayheadSeconds >= TimelineViewportStart && PlayheadSeconds <= TimelineViewportEndSeconds
                         ? PlayheadSeconds
                         : TimelineViewportStart + (TimelineViewportDurationSeconds / 2));
        var viewport = TimelineViewportMath.ZoomAround(
            DurationSeconds,
            TimelineZoom,
            TimelineViewportStart,
            TimelineZoom * factor,
            anchor);
        TimelineZoom = viewport.Zoom;
        TimelineViewportStart = viewport.Start;
    }

    public void FitTimeline()
    {
        TimelineZoom = 1;
        TimelineViewportStart = 0;
    }

    internal void SetWaveform(TimelineBitmapVisual? waveform)
    {
        Waveform = waveform;
    }

    public void Dispose()
    {
        SetWaveform(null);
    }

    public void Restore(SourceEdit edit, double gainDb, bool isMuted)
    {
        Restore(edit, gainDb, isMuted, MediaTime.Zero);
    }

    public void Restore(
        SourceEdit edit,
        double gainDb,
        bool isMuted,
        MediaTime timelineOffset)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.SourceDuration != Edit.SourceDuration)
        {
            throw new ArgumentException("The saved audio duration no longer matches the source.", nameof(edit));
        }

        if (timelineOffset < MediaTime.Zero || (!IsExternal && timelineOffset != MediaTime.Zero))
        {
            throw new ArgumentException("The saved audio timeline offset is invalid.", nameof(timelineOffset));
        }

        Edit = edit;
        _selectionStart = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].Start;
        _selectionEnd = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].End;
        GainDb = gainDb;
        IsMuted = isMuted;
        TimelineOffset = timelineOffset;
        RaiseSelectionChanged();
    }

    private MediaTime Quantize(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return MediaTime.Zero;
        }

        var bounded = Math.Clamp(seconds, 0, Edit.SourceDuration.TotalSeconds);
        var ticks = checked((long)Math.Round(
            bounded / _timelineQuantum.TotalSeconds,
            MidpointRounding.AwayFromZero));
        var value = _timelineQuantum * ticks;
        return value > Edit.SourceDuration ? Edit.SourceDuration : value;
    }

    private MediaTime QuantizeOffset(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return MediaTime.Zero;
        }

        var bounded = Math.Clamp(seconds, 0, MaximumTimelineOffsetSeconds);
        var ticks = checked((long)Math.Round(
            bounded / _timelineQuantum.TotalSeconds,
            MidpointRounding.AwayFromZero));
        return _timelineQuantum * ticks;
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionStartSeconds));
        OnPropertyChanged(nameof(SelectionEndSeconds));
        OnPropertyChanged(nameof(CanRemoveSelection));
    }

    private static string BuildDisplayName(ImportedMedia media, AudioStreamInfo stream)
    {
        var sourceName = Path.GetFileName(media.Probe.SourcePath);
        var language = string.IsNullOrWhiteSpace(stream.Language) ? null : stream.Language.ToUpperInvariant();
        var title = string.IsNullOrWhiteSpace(stream.Title) ? null : stream.Title;
        var detail = title ?? language ?? $"Track {stream.Index}";
        return media.IsExternalAudio ? sourceName : $"{sourceName} · {detail}";
    }

    private static string FormatTimestamp(MediaTime value)
    {
        var totalMilliseconds = Math.Max(0, (long)Math.Round(value.TotalSeconds * 1_000));
        var minutes = totalMilliseconds / 60_000;
        var seconds = (totalMilliseconds / 1_000) % 60;
        var milliseconds = totalMilliseconds % 1_000;
        return $"{minutes:00}:{seconds:00}.{milliseconds:000}";
    }

    private static MediaTime SecondsToDisplayTime(double seconds)
    {
        var milliseconds = checked((long)Math.Round(Math.Max(0, seconds) * 1_000));
        return new MediaTime(milliseconds, 1_000);
    }
}
