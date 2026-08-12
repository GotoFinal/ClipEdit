using System.Collections.Immutable;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed class AudioTrackViewModel : ViewModelBase
{
    private readonly MediaTime _timelineQuantum;
    private SourceEdit _edit;
    private MediaTime _playhead;
    private MediaTime _selectionStart;
    private MediaTime _selectionEnd;
    private double _gainDb;
    private bool _isMuted;

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
                OnPropertyChanged(nameof(CanRemoveSelection));
                OnPropertyChanged(nameof(OutputDurationText));
            }
        }
    }

    public ImmutableArray<MediaRange> KeptRanges => Edit.KeptRanges;

    public bool IsEdited => !Edit.IsUnedited || IsMuted || GainDb != 0;

    public double DurationSeconds => Edit.SourceDuration.TotalSeconds;

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

    public bool CanRemoveSelection =>
        _selectionStart < _selectionEnd &&
        Edit.KeptRanges.Any(range => _selectionStart < range.End && _selectionEnd > range.Start);

    public string OutputDurationText => $"Kept {FormatTimestamp(Edit.OutputDuration)}";

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
        RaiseSelectionChanged();
    }

    public void Restore(SourceEdit edit, double gainDb, bool isMuted)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.SourceDuration != Edit.SourceDuration)
        {
            throw new ArgumentException("The saved audio duration no longer matches the source.", nameof(edit));
        }

        Edit = edit;
        _selectionStart = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].Start;
        _selectionEnd = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].End;
        GainDb = gainDb;
        IsMuted = isMuted;
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
}
