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
    private ClipCanvasTransform _canvasTransform;
    private IReadOnlyList<TimelineThumbnailFrame> _timelineThumbnails = [];
    private HashSet<int> _excludedAudioLaneIndices;
    private bool _isTimelineLoading;

    public VideoClipViewModel(
        MediaItemViewModel source,
        SequenceClip model,
        CropRegion sourceWindow,
        ClipCanvasTransform? canvasTransform = null,
        IEnumerable<int>? excludedAudioLaneIndices = null)
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
        _canvasTransform = canvasTransform ?? ClipCanvasTransform.Identity;
        _excludedAudioLaneIndices = (excludedAudioLaneIndices ?? [])
            .Where(index => index >= 0)
            .ToHashSet();
    }

    public MediaItemViewModel Source { get; }

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

            if (!SetProperty(ref _sourceWindow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CropX));
            OnPropertyChanged(nameof(CropY));
            OnPropertyChanged(nameof(CropWidth));
            OnPropertyChanged(nameof(CropHeight));
            OnPropertyChanged(nameof(CropSizeText));
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

    public ClipCanvasTransform CanvasTransform
    {
        get => _canvasTransform;
        set
        {
            if (!SetProperty(ref _canvasTransform, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanvasOffsetX));
            OnPropertyChanged(nameof(CanvasOffsetY));
            OnPropertyChanged(nameof(CanvasScalePercent));
            OnPropertyChanged(nameof(CanvasScaleXPercent));
            OnPropertyChanged(nameof(CanvasScaleYPercent));
            OnPropertyChanged(nameof(CanvasRotationDegrees));
            OnPropertyChanged(nameof(CanvasTransformText));
        }
    }

    public double CanvasOffsetX
    {
        get => CanvasTransform.OffsetX;
        set => TrySetCanvasTransform(
            value,
            CanvasTransform.OffsetY,
            CanvasTransform.ScaleX,
            CanvasTransform.ScaleY,
            CanvasTransform.RotationDegrees);
    }

    public double CanvasOffsetY
    {
        get => CanvasTransform.OffsetY;
        set => TrySetCanvasTransform(
            CanvasTransform.OffsetX,
            value,
            CanvasTransform.ScaleX,
            CanvasTransform.ScaleY,
            CanvasTransform.RotationDegrees);
    }

    public double CanvasScalePercent
    {
        get => CanvasTransform.Scale * 100;
        set => TrySetCanvasTransform(
            CanvasTransform.OffsetX,
            CanvasTransform.OffsetY,
            value / 100,
            value / 100,
            CanvasTransform.RotationDegrees);
    }

    public double CanvasScaleXPercent
    {
        get => CanvasTransform.ScaleX * 100;
        set => TrySetCanvasTransform(
            CanvasTransform.OffsetX,
            CanvasTransform.OffsetY,
            value / 100,
            CanvasTransform.ScaleY,
            CanvasTransform.RotationDegrees);
    }

    public double CanvasScaleYPercent
    {
        get => CanvasTransform.ScaleY * 100;
        set => TrySetCanvasTransform(
            CanvasTransform.OffsetX,
            CanvasTransform.OffsetY,
            CanvasTransform.ScaleX,
            value / 100,
            CanvasTransform.RotationDegrees);
    }

    public double AudioGainDb
    {
        get => Model.AudioGainDb;
        set
        {
            var bounded = Math.Clamp(double.IsFinite(value) ? value : 0, -60, 12);
            if (bounded != Model.AudioGainDb)
            {
                ReplaceModel(Model.WithAudioGain(bounded));
            }
        }
    }

    public string AudioGainText =>
        AudioGainDb <= -59.95 ? "−∞ dB" : $"{AudioGainDb:+0.0;-0.0;0.0} dB";

    public int CanvasRotationDegrees
    {
        get => CanvasTransform.RotationDegrees;
        set => TrySetCanvasTransform(
            CanvasTransform.OffsetX,
            CanvasTransform.OffsetY,
            CanvasTransform.ScaleX,
            CanvasTransform.ScaleY,
            value);
    }

    public string CanvasTransformText =>
        $"X {CanvasTransform.OffsetX:0.#} · Y {CanvasTransform.OffsetY:0.#} · " +
        (CanvasTransform.HasUniformScale
            ? $"{CanvasTransform.ScaleX * 100:0.#}%"
            : $"W {CanvasTransform.ScaleX * 100:0.#}% · H {CanvasTransform.ScaleY * 100:0.#}%") +
        $" · {CanvasTransform.RotationDegrees}°";

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
        get => Model.TimelineStart;
        internal set
        {
            if (value != Model.TimelineStart)
            {
                ReplaceModel(Model.MoveTo(value));
            }
        }
    }

    public MediaTime TimelineEnd => Model.TimelineEnd;

    public double TimelineStartSeconds => TimelineStart.TotalSeconds;

    public double TimelineEndSeconds => TimelineEnd.TotalSeconds;

    public bool HasHeadHandle => Model.HasHeadHandle;

    public bool HasTailHandle => Model.HasTailHandle;

    public IReadOnlyList<MediaRange> PlaybackRanges => [Model.SourceRange];

    public bool IsTrimmed => Model.SourceRange != Model.AvailableRange;

    public IReadOnlyCollection<int> ExcludedAudioLaneIndices => _excludedAudioLaneIndices;

    public bool IncludesAudioLane(int laneIndex) => !_excludedAudioLaneIndices.Contains(laneIndex);

    public bool SetAudioLaneIncluded(int laneIndex, bool included)
    {
        if (laneIndex < 0)
        {
            return false;
        }

        var changed = included
            ? _excludedAudioLaneIndices.Remove(laneIndex)
            : _excludedAudioLaneIndices.Add(laneIndex);
        if (changed)
        {
            OnPropertyChanged(nameof(ExcludedAudioLaneIndices));
        }
        return changed;
    }

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
        new(Source, model, SourceWindow, CanvasTransform, ExcludedAudioLaneIndices);

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

    private void TrySetCanvasTransform(
        double offsetX,
        double offsetY,
        double scaleX,
        double scaleY,
        int rotationDegrees)
    {
        try
        {
            CanvasTransform = new ClipCanvasTransform(offsetX, offsetY, scaleX, scaleY, rotationDegrees);
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
        OnPropertyChanged(nameof(TimelineStart));
        OnPropertyChanged(nameof(TimelineStartSeconds));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(TimelineEnd));
        OnPropertyChanged(nameof(TimelineEndSeconds));
        OnPropertyChanged(nameof(HasHeadHandle));
        OnPropertyChanged(nameof(HasTailHandle));
        OnPropertyChanged(nameof(PlaybackRanges));
        OnPropertyChanged(nameof(IsTrimmed));
        OnPropertyChanged(nameof(SourceRangeText));
        OnPropertyChanged(nameof(AudioGainDb));
        OnPropertyChanged(nameof(AudioGainText));
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
