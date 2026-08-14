using System.ComponentModel;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.ViewModels;

public sealed class AudioTimelineSegmentViewModel : ViewModelBase, IDisposable
{
    public AudioTimelineSegmentViewModel(
        VideoClipViewModel? clip,
        string label,
        MediaTime timelineStart,
        MediaRange sourceRange,
        string? sourcePath = null,
        int streamIndex = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (timelineStart < MediaTime.Zero || sourceRange.IsEmpty)
        {
            throw new ArgumentException("An audio timeline segment needs a valid placement and source range.");
        }

        Clip = clip;
        Label = label;
        TimelineStart = timelineStart;
        SourceRange = sourceRange;
        SourcePath = sourcePath ?? clip?.SourcePath;
        StreamIndex = streamIndex;
        if (Clip is not null)
        {
            Clip.PropertyChanged += OnClipPropertyChanged;
        }
    }

    public VideoClipViewModel? Clip { get; }

    public Guid? ClipId => Clip?.Id;

    public string Label { get; }

    public MediaTime TimelineStart { get; }

    public MediaTime TimelineDuration => Clip?.Model.SourceDurationToTimeline(SourceRange.Duration) ?? SourceRange.Duration;

    public MediaTime TimelineEnd => TimelineStart + TimelineDuration;

    public MediaRange SourceRange { get; }

    public string? SourcePath { get; }

    public int StreamIndex { get; }

    public double TimelineStartSeconds => TimelineStart.TotalSeconds;

    public double TimelineEndSeconds => TimelineEnd.TotalSeconds;

    public double SourceStartSeconds => SourceRange.Start.TotalSeconds;

    public double SourceEndSeconds => SourceRange.End.TotalSeconds;

    public MediaTime TimelineTimeToSource(MediaTime timelineTime) => Clip is null
        ? SourceRange.Start + (timelineTime - TimelineStart)
        : SourceRange.Start + Clip.Model.TimelineDurationToSource(timelineTime - TimelineStart);

    public MediaTime SourceTimeToTimeline(MediaTime sourceTime) => Clip is null
        ? TimelineStart + (sourceTime - SourceRange.Start)
        : TimelineStart + Clip.Model.SourceDurationToTimeline(sourceTime - SourceRange.Start);

    public bool IsGainAdjustable => Clip is not null;

    public double GainDb
    {
        get => Clip?.AudioGainDb ?? 0;
        set
        {
            if (Clip is not null)
            {
                Clip.AudioGainDb = value;
            }
        }
    }

    public string GainText => Clip?.AudioGainText ?? "0.0 dB";

    public void Dispose()
    {
        if (Clip is not null)
        {
            Clip.PropertyChanged -= OnClipPropertyChanged;
        }
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is nameof(VideoClipViewModel.AudioGainDb) or
            nameof(VideoClipViewModel.AudioGainText))
        {
            OnPropertyChanged(nameof(GainDb));
            OnPropertyChanged(nameof(GainText));
        }
        else if (eventArgs.PropertyName is nameof(VideoClipViewModel.PlaybackSpeedPercent) or
                 nameof(VideoClipViewModel.Duration) or
                 nameof(VideoClipViewModel.TimelineEnd))
        {
            OnPropertyChanged(nameof(TimelineDuration));
            OnPropertyChanged(nameof(TimelineEnd));
            OnPropertyChanged(nameof(TimelineEndSeconds));
        }
    }
}
