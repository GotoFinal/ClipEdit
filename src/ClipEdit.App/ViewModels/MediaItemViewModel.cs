using ClipEdit.Application.Media;
using ClipEdit.Domain.Geometry;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed class MediaItemViewModel : ViewModelBase
{
    private ImportedMedia? _media;
    private string _statusText = "Waiting…";
    private string? _errorText;
    private bool _isProbing;
    private CropRegion _crop;

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
}
