using System.Collections.Immutable;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed class MediaItemViewModel : ViewModelBase
{
    private ImportedMedia? _media;
    private string _statusText = "Waiting…";
    private string? _errorText;
    private bool _isProbing;
    private CropRegion _crop;
    private SourceEdit? _edit;
    private MediaTime _timelineQuantum = new(1, 1_000);
    private MediaTime _playhead;
    private MediaTime _selectionStart;
    private MediaTime _selectionEnd;

    public MediaItemViewModel(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        SourcePath = sourcePath;
        DisplayName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = sourcePath;
        }
    }

    public string SourcePath { get; }

    public string DisplayName { get; }

    public ImportedMedia? Media
    {
        get => _media;
        private set
        {
            if (SetProperty(ref _media, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(HasVideo));
                OnPropertyChanged(nameof(HasAudio));
                OnPropertyChanged(nameof(IsExternalAudio));
                OnPropertyChanged(nameof(VideoSize));
                OnPropertyChanged(nameof(FrameStepSeconds));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(Detail));
            }
        }
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetProperty(ref _isProbing, value);
    }

    public bool IsReady => Media is not null;

    public bool HasVideo => Media?.HasVideo == true;

    public bool HasAudio => Media?.HasAudio == true;

    public bool IsExternalAudio => Media?.IsExternalAudio == true;

    public PixelSize VideoSize =>
        Media?.Probe.VideoStreams.FirstOrDefault()?.OrientedSize ?? new PixelSize(1, 1);

    public CropRegion Crop
    {
        get => _crop;
        set
        {
            if (value.SourceSize != VideoSize)
            {
                throw new ArgumentException("The crop must use this media item's oriented video size.", nameof(value));
            }

            if (SetProperty(ref _crop, value))
            {
                OnPropertyChanged(nameof(CropX));
                OnPropertyChanged(nameof(CropY));
                OnPropertyChanged(nameof(CropWidth));
                OnPropertyChanged(nameof(CropHeight));
                OnPropertyChanged(nameof(CropSizeText));
            }
        }
    }

    public int CropX
    {
        get => Crop.X;
        set => TrySetCrop(value, Crop.Y, Crop.Width, Crop.Height);
    }

    public int CropY
    {
        get => Crop.Y;
        set => TrySetCrop(Crop.X, value, Crop.Width, Crop.Height);
    }

    public int CropWidth
    {
        get => Crop.Width;
        set => TrySetCrop(Crop.X, Crop.Y, value, Crop.Height);
    }

    public int CropHeight
    {
        get => Crop.Height;
        set => TrySetCrop(Crop.X, Crop.Y, Crop.Width, value);
    }

    public string CropSizeText => $"{Crop.Width} × {Crop.Height}";

    public SourceEdit? Edit
    {
        get => _edit;
        private set
        {
            if (SetProperty(ref _edit, value))
            {
                OnPropertyChanged(nameof(KeptRanges));
                OnPropertyChanged(nameof(IsEdited));
                OnPropertyChanged(nameof(CanRemoveSelection));
                OnPropertyChanged(nameof(CanKeepSelectionOnly));
                OnPropertyChanged(nameof(OutputDurationText));
                OnPropertyChanged(nameof(SelectedExportDurationText));
            }
        }
    }

    public ImmutableArray<MediaRange> KeptRanges =>
        Edit?.KeptRanges ?? ImmutableArray<MediaRange>.Empty;

    public bool HasEditableDuration => Edit is not null;

    public bool IsEdited => Edit?.IsUnedited == false;

    public double SourceDurationSeconds => Edit?.SourceDuration.TotalSeconds ?? 0;

    public MediaTime Playhead
    {
        get => _playhead;
        set
        {
            var bounded = ClampToSource(value);
            if (SetProperty(ref _playhead, bounded))
            {
                OnPropertyChanged(nameof(PlayheadSeconds));
                OnPropertyChanged(nameof(PlayheadText));
            }
        }
    }

    public double PlayheadSeconds
    {
        get => Playhead.TotalSeconds;
        set => Playhead = QuantizeSeconds(value);
    }

    public string PlayheadText => FormatTimestamp(Playhead);

    public MediaTime SelectionStart
    {
        get => _selectionStart;
        private set
        {
            if (SetProperty(ref _selectionStart, ClampToSource(value)))
            {
                RaiseSelectionChanged();
            }
        }
    }

    public MediaTime SelectionEnd
    {
        get => _selectionEnd;
        private set
        {
            if (SetProperty(ref _selectionEnd, ClampToSource(value)))
            {
                RaiseSelectionChanged();
            }
        }
    }

    public double SelectionStartSeconds
    {
        get => SelectionStart.TotalSeconds;
        set => SelectionStart = QuantizeSeconds(value);
    }

    public double SelectionEndSeconds
    {
        get => SelectionEnd.TotalSeconds;
        set => SelectionEnd = QuantizeSeconds(value);
    }

    public bool CanRemoveSelection =>
        Edit is not null &&
        SelectionStart < SelectionEnd &&
        Edit.KeptRanges.Any(range =>
            SelectionStart < range.End && SelectionEnd > range.Start);

    public bool CanKeepSelectionOnly =>
        GetExportEdit() is { IsEmpty: false } selectedEdit &&
        Edit is not null &&
        !selectedEdit.KeptRanges.SequenceEqual(Edit.KeptRanges);

    public string SelectionRangeText =>
        $"{FormatTimestamp(SelectionStart)} – {FormatTimestamp(SelectionEnd)}";

    public string OutputDurationText => Edit is null
        ? "Unknown duration"
        : $"Output {FormatTimestamp(Edit.OutputDuration)}";

    public string SelectedExportDurationText => GetExportEdit() is not { } selectedEdit
        ? "Unknown duration"
        : $"Export {FormatTimestamp(selectedEdit.OutputDuration)}";

    public double FrameStepSeconds
    {
        get
        {
            var frameRate = Media?.Probe.VideoStreams.FirstOrDefault()?.AverageFrameRate;
            return frameRate is { IsZero: false }
                ? frameRate.Value.Denominator / (double)frameRate.Value.Numerator
                : _timelineQuantum.TotalSeconds;
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string Summary
    {
        get
        {
            if (Media is null)
            {
                return StatusText;
            }

            var video = Media.Probe.VideoStreams.FirstOrDefault();
            if (video is not null)
            {
                var frameRate = video.AverageFrameRate ?? video.NominalFrameRate;
                var frameRateText = frameRate is null
                    ? string.Empty
                    : $" · {frameRate.Value.FramesPerSecond:0.###} fps";
                return $"{video.OrientedSize.Width}×{video.OrientedSize.Height} · " +
                       $"{video.CodecName.ToUpperInvariant()}{frameRateText}";
            }

            var audio = Media.Probe.AudioStreams.First();
            var channelText = audio.ChannelLayout ??
                              (audio.ChannelCount is null ? "audio" : $"{audio.ChannelCount} ch");
            return $"{audio.CodecName.ToUpperInvariant()} · {channelText}";
        }
    }

    public string Detail
    {
        get
        {
            if (Media is null)
            {
                return ErrorText ?? StatusText;
            }

            return $"{FormatDuration(Media.Probe.Duration)} · " +
                   $"{FormatSize(Media.Probe.FileSizeBytes)}";
        }
    }

    public async Task ProbeAsync(
        ImportMediaUseCase importMedia,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(importMedia);

        IsProbing = true;
        ErrorText = null;
        StatusText = "Probing…";
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Detail));

        try
        {
            Media = await importMedia.ExecuteAsync(SourcePath, cancellationToken);
            var video = Media.Probe.VideoStreams.FirstOrDefault();
            if (video is not null)
            {
                Crop = CropRegion.FullFrame(video.OrientedSize);
                InitializeEditing(video);
            }
            StatusText = "Ready";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Canceled";
        }
        catch (MediaProbeException exception)
        {
            StatusText = "Could not import";
            ErrorText = exception.Message;
        }
        catch (Exception exception)
        {
            StatusText = "Could not import";
            ErrorText = $"Unexpected import error: {exception.Message}";
        }
        finally
        {
            IsProbing = false;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Detail));
        }
    }

    public void MarkSelectionStart()
    {
        SelectionStart = Playhead;
    }

    public void MarkSelectionEnd()
    {
        SelectionEnd = Playhead;
    }

    public bool RemoveSelection()
    {
        if (!CanRemoveSelection || Edit is null)
        {
            return false;
        }

        var removal = new MediaRange(SelectionStart, SelectionEnd);
        Edit = Edit.Remove(removal);
        Playhead = removal.End;
        SelectionStart = Playhead;
        SelectionEnd = Playhead;
        return true;
    }

    public bool KeepSelectionOnly()
    {
        if (!CanKeepSelectionOnly || Edit is null)
        {
            return false;
        }

        Edit = Edit.KeepOnly(new MediaRange(SelectionStart, SelectionEnd));
        Playhead = Edit.KeptRanges.IsEmpty ? SelectionStart : Edit.KeptRanges[0].Start;
        return true;
    }

    public SourceEdit? GetExportEdit()
    {
        if (Edit is null || SelectionStart >= SelectionEnd)
        {
            return Edit;
        }

        return Edit.KeepOnly(new MediaRange(SelectionStart, SelectionEnd));
    }

    public void ResetCuts()
    {
        if (Edit is null)
        {
            return;
        }

        Edit = Edit.Reset();
        SelectionStart = MediaTime.Zero;
        SelectionEnd = Edit.SourceDuration;
    }

    public void ResetCrop()
    {
        if (HasVideo)
        {
            Crop = CropRegion.FullFrame(VideoSize);
        }
    }

    public void RestoreEditing(CropRegion crop, SourceEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!HasVideo || Edit is null)
        {
            throw new InvalidOperationException("Only an imported video can restore saved edit decisions.");
        }

        if (crop.SourceSize != VideoSize)
        {
            throw new ArgumentException("The saved crop does not match the imported video dimensions.", nameof(crop));
        }

        if (edit.SourceDuration != Edit.SourceDuration)
        {
            throw new ArgumentException("The saved edit duration does not match the imported video.", nameof(edit));
        }

        Crop = crop;
        Edit = edit;
        SelectionStart = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].Start;
        SelectionEnd = edit.KeptRanges.IsEmpty ? MediaTime.Zero : edit.KeptRanges[0].End;
        Playhead = SelectionStart;
    }

    private static string FormatDuration(ClipEdit.Domain.Timeline.MediaTime? duration)
    {
        if (duration is null)
        {
            return "Unknown duration";
        }

        var totalSeconds = duration.Value.Numerator / duration.Value.Denominator;
        var hours = totalSeconds / 3_600;
        var minutes = (totalSeconds % 3_600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return "Unknown size";
        }

        const double gibibyte = 1024d * 1024d * 1024d;
        const double mebibyte = 1024d * 1024d;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.##} GiB"
            : $"{bytes / mebibyte:0.#} MiB";
    }

    private void TrySetCrop(int x, int y, int width, int height)
    {
        try
        {
            Crop = new CropRegion(VideoSize, x, y, width, height);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Numeric input is allowed to be temporarily invalid while the user edits it.
        }
    }

    private void InitializeEditing(VideoStreamInfo video)
    {
        var duration = Media?.Probe.Duration ?? video.Duration;
        if (duration is null || duration <= MediaTime.Zero)
        {
            return;
        }

        if (video.TimeBase is { } timeBase && timeBase > MediaTime.Zero)
        {
            _timelineQuantum = timeBase;
        }

        Edit = new SourceEdit(duration.Value);
        SelectionStart = MediaTime.Zero;
        SelectionEnd = duration.Value;
        Playhead = Min(new MediaTime(1, 1), duration.Value / 10);
        OnPropertyChanged(nameof(HasEditableDuration));
        OnPropertyChanged(nameof(SourceDurationSeconds));
    }

    private MediaTime QuantizeSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || Edit is null)
        {
            return MediaTime.Zero;
        }

        var bounded = Math.Clamp(seconds, 0, Edit.SourceDuration.TotalSeconds);
        var ticks = checked((long)Math.Round(
            bounded / _timelineQuantum.TotalSeconds,
            MidpointRounding.AwayFromZero));
        return ClampToSource(_timelineQuantum * ticks);
    }

    private MediaTime ClampToSource(MediaTime value)
    {
        if (value < MediaTime.Zero || Edit is null)
        {
            return MediaTime.Zero;
        }

        return value > Edit.SourceDuration ? Edit.SourceDuration : value;
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionStartSeconds));
        OnPropertyChanged(nameof(SelectionEndSeconds));
        OnPropertyChanged(nameof(SelectionRangeText));
        OnPropertyChanged(nameof(CanRemoveSelection));
        OnPropertyChanged(nameof(CanKeepSelectionOnly));
        OnPropertyChanged(nameof(SelectedExportDurationText));
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

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;
}
