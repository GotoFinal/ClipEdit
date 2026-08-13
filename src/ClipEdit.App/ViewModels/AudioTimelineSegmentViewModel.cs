using System.ComponentModel;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.ViewModels;

public sealed class AudioTimelineSegmentViewModel : ViewModelBase, IDisposable
{
    public AudioTimelineSegmentViewModel(
        VideoClipViewModel? clip,
        string label,
        MediaTime timelineStart,
        MediaRange sourceRange)
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
        if (Clip is not null)
        {
            Clip.PropertyChanged += OnClipPropertyChanged;
        }
    }

    public VideoClipViewModel? Clip { get; }

    public Guid? ClipId => Clip?.Id;

    public string Label { get; }

    public MediaTime TimelineStart { get; }

    public MediaTime TimelineEnd => TimelineStart + SourceRange.Duration;

    public MediaRange SourceRange { get; }

    public double TimelineStartSeconds => TimelineStart.TotalSeconds;

    public double TimelineEndSeconds => TimelineEnd.TotalSeconds;

    public double SourceStartSeconds => SourceRange.Start.TotalSeconds;

    public double SourceEndSeconds => SourceRange.End.TotalSeconds;

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
    }
}
