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
    private IReadOnlyList<TimelineBitmapVisual> _waveforms = [];
    private IReadOnlyList<AudioTimelineSegmentViewModel> _timelineSegments = [];
    private IReadOnlyList<AudioTimelineSegmentViewModel> _adjustableTimelineSegments = [];
    private VideoClipViewModel? _contextualGainClip;
    private int _waveformVisualRevision;
    private ImmutableArray<MediaRange> _timelineKeptRanges = [];
    private MediaTime _timelinePlayhead;
    private MediaTime _timelineSelectionStart;
    private MediaTime _timelineSelectionEnd;
    private double _timelineDurationSeconds;
    private bool _timelineFreeViewport;
    private bool _isWaveformDecimated;
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
                RebuildTimelineKeptRanges();
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
                if (_contextualGainClip is null)
                {
                    OnPropertyChanged(nameof(ContextualGainDb));
                    OnPropertyChanged(nameof(ContextualGainText));
                }
                IncrementWaveformVisualRevision();
            }
        }
    }

    public string GainText => GainDb <= -59.95 ? "−∞ dB" : $"{GainDb:+0.0;-0.0;0.0} dB";

    public double ContextualGainDb
    {
        get => _contextualGainClip?.AudioGainDb ?? GainDb;
        set
        {
            if (_contextualGainClip is { } clip)
            {
                clip.AudioGainDb = value;
            }
            else
            {
                GainDb = value;
            }
        }
    }

    public string ContextualGainText => _contextualGainClip?.AudioGainText ?? GainText;

    public string ContextualGainLabel => _contextualGainClip is null ? "Track gain" : "Clip gain";

    public string ContextualGainTargetText => _contextualGainClip is null
        ? "Adjust the whole audio track"
        : $"Adjust selected clip: {_contextualGainClip.DisplayName}";

    public int WaveformVisualRevision => _waveformVisualRevision;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(IsEdited));
                RebuildTimelineKeptRanges();
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

    public double TimelineDurationSeconds => _timelineDurationSeconds;

    public IReadOnlyList<AudioTimelineSegmentViewModel> TimelineSegments => _timelineSegments;

    public IReadOnlyList<AudioTimelineSegmentViewModel> AdjustableTimelineSegments =>
        _adjustableTimelineSegments;

    public ImmutableArray<MediaRange> TimelineKeptRanges => _timelineKeptRanges;

    public bool TimelineFreeViewport => _timelineFreeViewport;

    public double TimelinePlayheadSeconds
    {
        get => _timelinePlayhead.TotalSeconds;
        set
        {
            if (SetProperty(ref _timelinePlayhead, QuantizeTimeline(value)))
            {
                OnPropertyChanged(nameof(TimelinePlayheadText));
            }
        }
    }

    public string TimelinePlayheadText => FormatTimestamp(_timelinePlayhead);

    public double TimelineSelectionStartSeconds
    {
        get => _timelineSelectionStart.TotalSeconds;
        set
        {
            if (SetProperty(ref _timelineSelectionStart, QuantizeTimeline(value)))
            {
                RaiseTimelineSelectionChanged();
            }
        }
    }

    public double TimelineSelectionEndSeconds
    {
        get => _timelineSelectionEnd.TotalSeconds;
        set
        {
            if (SetProperty(ref _timelineSelectionEnd, QuantizeTimeline(value)))
            {
                RaiseTimelineSelectionChanged();
            }
        }
    }

    public bool CanSilenceTimelineSelection =>
        _timelineSelectionStart < _timelineSelectionEnd &&
        TimelineKeptRanges.Any(range =>
            _timelineSelectionStart < range.End && _timelineSelectionEnd > range.Start);

    public string TimelineSelectionText =>
        $"{FormatTimestamp(_timelineSelectionStart)} – {FormatTimestamp(_timelineSelectionEnd)}";

    public string TimelineViewportLabel => "Synced to video timeline";

    public bool IsWaveformDecimated
    {
        get => _isWaveformDecimated;
        internal set => SetProperty(ref _isWaveformDecimated, value);
    }


    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            var zoom = TimelineViewportMath.ClampZoom(value, TimelineFreeViewport);
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
            var start = TimelineViewportMath.ClampStart(
                TimelineDurationSeconds,
                TimelineZoom,
                value,
                TimelineFreeViewport);
            if (SetProperty(ref _timelineViewportStart, start))
            {
                OnPropertyChanged(nameof(TimelineViewportEndSeconds));
                OnPropertyChanged(nameof(TimelineViewportText));
            }
        }
    }
    public bool SilenceTimelineSelection()
    {
        if (!CanSilenceTimelineSelection)
        {
            return false;
        }

        var edit = Edit;
        foreach (var segment in TimelineSegments)
        {
            var overlapStart = Max(_timelineSelectionStart, segment.TimelineStart);
            var overlapEnd = Min(_timelineSelectionEnd, segment.TimelineEnd);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            var sourceStart = segment.SourceRange.Start + (overlapStart - segment.TimelineStart);
            var sourceEnd = segment.SourceRange.Start + (overlapEnd - segment.TimelineStart);
            var removal = new MediaRange(
                Quantize(sourceStart.TotalSeconds),
                Quantize(sourceEnd.TotalSeconds));
            if (!removal.IsEmpty)
            {
                edit = edit.Remove(removal);
            }
        }

        if (ReferenceEquals(edit, Edit) || edit == Edit)
        {
            return false;
        }

        Edit = edit;
        return true;
    }

    internal void SetTimelineSegments(IReadOnlyList<AudioTimelineSegmentViewModel> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var previous = _timelineSegments;
        _timelineSegments = segments;
        _adjustableTimelineSegments = segments.Where(segment => segment.IsGainAdjustable).ToArray();
        foreach (var segment in previous)
        {
            segment.PropertyChanged -= OnTimelineSegmentPropertyChanged;
            segment.Dispose();
        }

        foreach (var segment in _timelineSegments)
        {
            segment.PropertyChanged += OnTimelineSegmentPropertyChanged;
        }

        OnPropertyChanged(nameof(TimelineSegments));
        OnPropertyChanged(nameof(AdjustableTimelineSegments));
        RebuildTimelineKeptRanges();
    }

    internal void SetContextualGainClip(VideoClipViewModel? clip)
    {
        if (ReferenceEquals(_contextualGainClip, clip))
        {
            return;
        }

        if (_contextualGainClip is not null)
        {
            _contextualGainClip.PropertyChanged -= OnContextualGainClipPropertyChanged;
        }

        _contextualGainClip = clip;
        if (_contextualGainClip is not null)
        {
            _contextualGainClip.PropertyChanged += OnContextualGainClipPropertyChanged;
        }

        OnPropertyChanged(nameof(ContextualGainDb));
        OnPropertyChanged(nameof(ContextualGainText));
        OnPropertyChanged(nameof(ContextualGainLabel));
        OnPropertyChanged(nameof(ContextualGainTargetText));
    }

    internal void SynchronizeTimelineState(
        double durationSeconds,
        double playheadSeconds,
        double selectionStartSeconds,
        double selectionEndSeconds,
        double zoom,
        double viewportStart,
        bool freeViewport)
    {
        var duration = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        var freeChanged = SetProperty(ref _timelineFreeViewport, freeViewport, nameof(TimelineFreeViewport));
        var durationChanged = SetProperty(
            ref _timelineDurationSeconds,
            duration,
            nameof(TimelineDurationSeconds));
        if (freeChanged || durationChanged)
        {
            OnPropertyChanged(nameof(TimelineViewportDurationSeconds));
            OnPropertyChanged(nameof(TimelineViewportEndSeconds));
            OnPropertyChanged(nameof(CanZoomTimelineOut));
        }

        TimelineZoom = zoom;
        TimelineViewportStart = viewportStart;
        TimelinePlayheadSeconds = playheadSeconds;
        TimelineSelectionStartSeconds = selectionStartSeconds;
        TimelineSelectionEndSeconds = selectionEndSeconds;
    }

    private void RebuildTimelineKeptRanges()
    {
        if (IsMuted)
        {
            _timelineKeptRanges = [];
            OnPropertyChanged(nameof(TimelineKeptRanges));
            OnPropertyChanged(nameof(CanSilenceTimelineSelection));
            return;
        }
        var mapped = new List<MediaRange>();
        foreach (var segment in TimelineSegments)
        {
            foreach (var kept in KeptRanges)
            {
                var sourceStart = Max(segment.SourceRange.Start, kept.Start);
                var sourceEnd = Min(segment.SourceRange.End, kept.End);
                if (sourceEnd <= sourceStart)
                {
                    continue;
                }

                var timelineStart = segment.TimelineStart + (sourceStart - segment.SourceRange.Start);
                mapped.Add(new MediaRange(
                    timelineStart,
                    timelineStart + (sourceEnd - sourceStart)));
            }
        }

        var merged = ImmutableArray.CreateBuilder<MediaRange>();
        foreach (var range in mapped.OrderBy(range => range.Start))
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            if (range.End > merged[^1].End)
            {
                merged[^1] = new MediaRange(merged[^1].Start, range.End);
            }
        }

        _timelineKeptRanges = merged.ToImmutable();
        OnPropertyChanged(nameof(TimelineKeptRanges));
        OnPropertyChanged(nameof(CanSilenceTimelineSelection));
    }

    private void RaiseTimelineSelectionChanged()
    {
        OnPropertyChanged(nameof(TimelineSelectionStartSeconds));
        OnPropertyChanged(nameof(TimelineSelectionEndSeconds));
        OnPropertyChanged(nameof(TimelineSelectionText));
        OnPropertyChanged(nameof(CanSilenceTimelineSelection));
    }

    private MediaTime QuantizeTimeline(double seconds)
    {
        var bounded = Math.Clamp(
            double.IsFinite(seconds) ? seconds : 0,
            0,
            TimelineDurationSeconds);
        return new MediaTime(
            checked((long)Math.Round(bounded * 1_000_000, MidpointRounding.AwayFromZero)),
            1_000_000);
    }

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;

    private static MediaTime Max(MediaTime left, MediaTime right) => left >= right ? left : right;


    public double TimelineViewportDurationSeconds =>
        TimelineDurationSeconds <= 0
            ? 0
            : TimelineViewportMath.VisibleDuration(
                TimelineDurationSeconds, TimelineZoom, TimelineFreeViewport);

    public double TimelineViewportEndSeconds =>
        TimelineViewportStart + TimelineViewportDurationSeconds;

    public string TimelineZoomText => $"{TimelineZoom:0.#}×";

    public string TimelineViewportText =>
        $"{FormatTimestamp(SecondsToDisplayTime(TimelineViewportStart))} – " +
        $"{FormatTimestamp(SecondsToDisplayTime(TimelineViewportEndSeconds))}";

    public bool CanZoomTimelineIn => TimelineZoom < TimelineViewportMath.MaximumZoom;

    public bool CanZoomTimelineOut =>
        TimelineZoom > (TimelineFreeViewport ? TimelineViewportMath.MinimumFreeZoom : 1);

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

    public IReadOnlyList<TimelineBitmapVisual> Waveforms
    {
        get => _waveforms;
        private set
        {
            var previous = _waveforms;
            if (!SetProperty(ref _waveforms, value))
            {
                return;
            }

            foreach (var visual in previous)
            {
                visual.Dispose();
            }

            OnPropertyChanged(nameof(HasWaveform));
        }
    }

    public bool HasWaveform => Waveform is not null || Waveforms.Count > 0;
    internal void SetWaveforms(IReadOnlyList<TimelineBitmapVisual> waveforms)
    {
        Waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
    }


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
            TimelineDurationSeconds,
            TimelineZoom,
            TimelineViewportStart,
            TimelineZoom * factor,
            anchor,
            TimelineFreeViewport);
        TimelineZoom = viewport.Zoom;
        TimelineViewportStart = viewport.Start;
    }

    public void FitTimeline()
    {
        TimelineZoom = 1;
        TimelineViewportStart = TimelineViewportMath.ClampStart(TimelineDurationSeconds, 1, 0, TimelineFreeViewport);
    }

    internal void SetWaveform(TimelineBitmapVisual? waveform)
    {
        Waveform = waveform;
    }

    public void Dispose()
    {
        SetContextualGainClip(null);
        SetWaveforms([]);
        foreach (var segment in _timelineSegments)
        {
            segment.PropertyChanged -= OnTimelineSegmentPropertyChanged;
            segment.Dispose();
        }
        _timelineSegments = [];
        _adjustableTimelineSegments = [];
        SetWaveform(null);
    }

    private void OnContextualGainClipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is nameof(VideoClipViewModel.AudioGainDb) or
            nameof(VideoClipViewModel.AudioGainText))
        {
            OnPropertyChanged(nameof(ContextualGainDb));
            OnPropertyChanged(nameof(ContextualGainText));
            IncrementWaveformVisualRevision();
        }
    }

    private void OnTimelineSegmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is nameof(AudioTimelineSegmentViewModel.GainDb) or
            nameof(AudioTimelineSegmentViewModel.GainText))
        {
            IncrementWaveformVisualRevision();
        }
    }

    private void IncrementWaveformVisualRevision()
    {
        unchecked
        {
            _waveformVisualRevision++;
        }
        OnPropertyChanged(nameof(WaveformVisualRevision));
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
