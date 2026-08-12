using Avalonia.Media.Imaging;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.ViewModels;

/// <summary>
/// One editable instance of a source asset in the ordered video sequence.
/// </summary>
public sealed class VideoClipViewModel : ViewModelBase, IDisposable
{
    private SequenceClip _model;
    private CropRegion _sourceWindow;
    private MediaTime _timelineStart;
    private IReadOnlyList<TimelineThumbnailFrame> _timelineThumbnails = [];
    private bool _isTimelineLoading;

    public VideoClipViewModel(
        MediaItemViewModel source,
        SequenceClip model,
        CropRegion sourceWindow)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (model.SourceId != source.Id)
        {
            throw new ArgumentException("The clip must refer to the supplied source asset.", nameof(model));
        }

        if (sourceWindow.SourceSize != source.VideoSize)
        {
            throw new ArgumentException("The source window must use the source asset dimensions.", nameof(sourceWindow));
        }

        _model = model;
        _sourceWindow = sourceWindow;
    }

    public MediaItemViewModel Source { get; }

    public event EventHandler? SourceWindowResized;

    public SequenceClip Model
    {
        get => _model;
        private set
        {
            if (!SetProperty(ref _model, value))
            {
                return;
            }

            RaiseRangeChanged();
        }
    }

    public Guid Id => Model.Id;

    public string DisplayName => Source.DisplayName;

    public string SourcePath => Source.SourcePath;

    public PixelSize VideoSize => Source.VideoSize;

    public CropRegion SourceWindow
    {
        get => _sourceWindow;
        set
        {
            if (value.SourceSize != VideoSize)
            {
                throw new ArgumentException("The source window must use this clip's source size.", nameof(value));
            }

            var wasResized = value.Width != _sourceWindow.Width || value.Height != _sourceWindow.Height;
            if (!SetProperty(ref _sourceWindow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CropX));
            OnPropertyChanged(nameof(CropY));
            OnPropertyChanged(nameof(CropWidth));
            OnPropertyChanged(nameof(CropHeight));
            OnPropertyChanged(nameof(CropSizeText));
            if (wasResized)
            {
                SourceWindowResized?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int CropX
    {
        get => SourceWindow.X;
        set => TrySetSourceWindow(value, SourceWindow.Y, SourceWindow.Width, SourceWindow.Height);
    }

    public int CropY
    {
        get => SourceWindow.Y;
        set => TrySetSourceWindow(SourceWindow.X, value, SourceWindow.Width, SourceWindow.Height);
    }

    public int CropWidth
    {
        get => SourceWindow.Width;
        set => TrySetSourceWindow(SourceWindow.X, SourceWindow.Y, value, SourceWindow.Height);
    }

    public int CropHeight
    {
        get => SourceWindow.Height;
        set => TrySetSourceWindow(SourceWindow.X, SourceWindow.Y, SourceWindow.Width, value);
    }

    public string CropSizeText => $"{SourceWindow.Width} × {SourceWindow.Height}";

    public MediaTime SourceStart => Model.SourceRange.Start;

    public MediaTime SourceEnd => Model.SourceRange.End;

    public double SourceStartSeconds
    {
        get => SourceStart.TotalSeconds;
        set
        {
            var requested = QuantizeSeconds(value);
            if (requested != SourceStart)
            {
                ReplaceModel(Model.TrimStart(requested));
            }
        }
    }

    public double SourceEndSeconds
    {
        get => SourceEnd.TotalSeconds;
        set
        {
            var requested = QuantizeSeconds(value);
            if (requested != SourceEnd)
            {
                ReplaceModel(Model.TrimEnd(requested));
            }
        }
    }

    public MediaTime Duration => Model.Duration;

    public double DurationSeconds => Duration.TotalSeconds;

    public MediaTime TimelineStart
    {
        get => _timelineStart;
        internal set
        {
            if (SetProperty(ref _timelineStart, value))
            {
                OnPropertyChanged(nameof(TimelineStartSeconds));
                OnPropertyChanged(nameof(TimelineEnd));
                OnPropertyChanged(nameof(TimelineEndSeconds));
            }
        }
    }

    public MediaTime TimelineEnd => TimelineStart + Duration;

    public double TimelineStartSeconds => TimelineStart.TotalSeconds;

    public double TimelineEndSeconds => TimelineEnd.TotalSeconds;

    public bool HasHeadHandle => Model.HasHeadHandle;

    public bool HasTailHandle => Model.HasTailHandle;

    public IReadOnlyList<MediaRange> PlaybackRanges => [Model.SourceRange];

    public bool IsTrimmed => Model.SourceRange != Model.AvailableRange;

    public IReadOnlyList<TimelineThumbnailFrame> TimelineThumbnails
    {
        get => _timelineThumbnails;
        private set
        {
            var previous = _timelineThumbnails;
            if (!SetProperty(ref _timelineThumbnails, value))
            {
                return;
            }

            foreach (var thumbnail in previous)
            {
                thumbnail.Dispose();
            }
        }
    }

    public bool IsTimelineLoading
    {
        get => _isTimelineLoading;
        internal set => SetProperty(ref _isTimelineLoading, value);
    }

    public string SourceRangeText =>
        $"{FormatTimestamp(SourceStart)} – {FormatTimestamp(SourceEnd)}";

    public void ReplaceModel(SequenceClip model)
    {
        if (model.Id != Id || model.SourceId != Source.Id)
        {
            throw new ArgumentException("Replacement clip identity does not match.", nameof(model));
        }

        Model = model;
    }

    public VideoClipViewModel CreateSibling(SequenceClip model) =>
        new(Source, model, SourceWindow);

    public void SetTimelineThumbnails(IReadOnlyList<TimelineThumbnailFrame> thumbnails) =>
        TimelineThumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));

    public void Dispose() => SetTimelineThumbnails([]);

    private void TrySetSourceWindow(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            SourceWindow = new CropRegion(VideoSize, x, y, width, height);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private void RaiseRangeChanged()
    {
        OnPropertyChanged(nameof(SourceStart));
        OnPropertyChanged(nameof(SourceEnd));
        OnPropertyChanged(nameof(SourceStartSeconds));
        OnPropertyChanged(nameof(SourceEndSeconds));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(TimelineEnd));
        OnPropertyChanged(nameof(TimelineEndSeconds));
        OnPropertyChanged(nameof(HasHeadHandle));
        OnPropertyChanged(nameof(HasTailHandle));
        OnPropertyChanged(nameof(PlaybackRanges));
        OnPropertyChanged(nameof(IsTrimmed));
        OnPropertyChanged(nameof(SourceRangeText));
    }

    private MediaTime QuantizeSeconds(double seconds)
    {
        var bounded = Math.Clamp(
            double.IsFinite(seconds) ? seconds : 0,
            Model.AvailableRange.Start.TotalSeconds,
            Model.AvailableRange.End.TotalSeconds);
        var step = Math.Max(0.000001, Source.FrameStepSeconds);
        var quantized = Math.Round(bounded / step) * step;
        return new MediaTime(checked((long)Math.Round(quantized * 1_000_000)), 1_000_000);
    }

    private static string FormatTimestamp(MediaTime value)
    {
        var totalMilliseconds = Math.Max(0, (long)Math.Round(value.TotalSeconds * 1_000));
        var hours = totalMilliseconds / 3_600_000;
        var minutes = (totalMilliseconds / 60_000) % 60;
        var seconds = (totalMilliseconds / 1_000) % 60;
        var milliseconds = totalMilliseconds % 1_000;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}.{milliseconds:000}"
            : $"{minutes:00}:{seconds:00}.{milliseconds:000}";
    }
}
