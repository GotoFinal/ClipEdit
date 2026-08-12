using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Frames;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly HashSet<string> _knownPaths = new(PathComparer);
    private readonly ImportMediaUseCase? _importMedia;
    private readonly IFrameDecoder? _frameDecoder;
    private MediaItemViewModel? _selectedMedia;
    private bool _isBusy;
    private bool _isPreviewLoading;
    private Bitmap? _previewImage;
    private string? _previewErrorText;
    private CancellationTokenSource? _previewCancellation;
    private string _statusText = "Ready";

    public MainWindowViewModel(IMediaProbe? mediaProbe, IFrameDecoder? frameDecoder = null)
    {
        _frameDecoder = frameDecoder;
        if (mediaProbe is not null)
        {
            _importMedia = new ImportMediaUseCase(mediaProbe);
        }
        else
        {
            StatusText = "ffprobe was not found. Configure CLIPEDIT_FFPROBE_PATH to import media.";
        }
    }

    public string ProductName => "ClipEdit";

    public string WorkspaceTitle => ShowTimeline ? "Timeline edit" : "Create a short clip";

    public string EmptyStateTitle => "Drop a video to begin";

    public string EmptyStateDescription =>
        "Your source stays untouched. ClipEdit will reveal trimming and crop controls after import.";

    public string SupportedMediaHint => "Video and audio files supported by the local media engine";

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = [];

    public MediaItemViewModel? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (SetProperty(ref _selectedMedia, value))
            {
                RaiseWorkspaceStateChanged();
                StartPreviewRefresh(value);
            }
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var previous = _previewImage;
            if (SetProperty(ref _previewImage, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasPreviewImage));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
            }
        }
    }

    public bool HasPreviewImage => PreviewImage is not null;

    public bool ShowPreviewPlaceholder => !HasPreviewImage;

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetProperty(ref _isPreviewLoading, value);
    }

    public string? PreviewErrorText
    {
        get => _previewErrorText;
        private set
        {
            if (SetProperty(ref _previewErrorText, value))
            {
                OnPropertyChanged(nameof(HasPreviewError));
            }
        }
    }

    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewErrorText);

    public bool IsImportAvailable => _importMedia is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasReadyMedia => MediaItems.Any(item => item.IsReady);

    public bool ShowEmptyState => !HasReadyMedia;

    public bool ShowQuickWorkspace => HasReadyMedia;

    public bool ShowTimeline => MediaItems.Count(item => item.HasVideo) > 1;

    public bool ShowAudioMixer => MediaItems.Any(item => item.IsExternalAudio);

    public bool ShowRangeStrip => ShowQuickWorkspace && !ShowTimeline;

    public IEnumerable<MediaItemViewModel> VideoItems => MediaItems.Where(item => item.HasVideo);

    public IEnumerable<MediaItemViewModel> ExternalAudioItems => MediaItems.Where(item => item.IsExternalAudio);

    public string EditingModeText => ShowTimeline ? "TIMELINE" : "QUICK EDIT";

    public string CropSizeText
    {
        get
        {
            var video = SelectedMedia?.Media?.Probe.VideoStreams.FirstOrDefault();
            return video is null
                ? "No video selected"
                : $"{video.OrientedSize.Width} × {video.OrientedSize.Height}";
        }
    }

    public string AudioSummaryText
    {
        get
        {
            var audioStreams = SelectedMedia?.Media?.Probe.AudioStreams.ToArray() ?? [];
            return audioStreams.Length switch
            {
                0 => "No embedded audio",
                1 => BuildAudioStreamText(audioStreams[0]),
                _ => $"{audioStreams.Length} embedded audio tracks",
            };
        }
    }

    public async Task ImportFilesAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        if (_importMedia is null)
        {
            StatusText = "Media import is unavailable because ffprobe was not found.";
            return;
        }

        var pendingItems = new List<MediaItemViewModel>();
        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!_knownPaths.Add(fullPath))
            {
                continue;
            }

            var item = new MediaItemViewModel(fullPath);
            MediaItems.Add(item);
            pendingItems.Add(item);
        }

        if (pendingItems.Count == 0)
        {
            StatusText = "No new local media files were selected.";
            return;
        }

        IsBusy = true;
        StatusText = pendingItems.Count == 1
            ? "Inspecting media…"
            : $"Inspecting {pendingItems.Count} media files…";

        foreach (var item in pendingItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await item.ProbeAsync(_importMedia, cancellationToken);
            if (item.IsReady && SelectedMedia is null)
            {
                SelectedMedia = item;
            }

            RaiseWorkspaceStateChanged();
        }

        IsBusy = false;
        var readyCount = pendingItems.Count(item => item.IsReady);
        StatusText = readyCount == pendingItems.Count
            ? $"{readyCount} media file{(readyCount == 1 ? string.Empty : "s")} ready"
            : $"{readyCount} of {pendingItems.Count} media files ready";
        RaiseWorkspaceStateChanged();
    }

    public void Dispose()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        PreviewImage = null;
    }

    private void RaiseWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasReadyMedia));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowQuickWorkspace));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(ShowAudioMixer));
        OnPropertyChanged(nameof(ShowRangeStrip));
        OnPropertyChanged(nameof(VideoItems));
        OnPropertyChanged(nameof(ExternalAudioItems));
        OnPropertyChanged(nameof(EditingModeText));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(CropSizeText));
        OnPropertyChanged(nameof(AudioSummaryText));
    }

    private static string BuildAudioStreamText(AudioStreamInfo audio)
    {
        var language = string.IsNullOrWhiteSpace(audio.Language) ? "Audio" : audio.Language.ToUpperInvariant();
        var layout = audio.ChannelLayout ??
                     (audio.ChannelCount is null ? "unknown layout" : $"{audio.ChannelCount} ch");
        return $"{language} · {audio.CodecName.ToUpperInvariant()} · {layout}";
    }

    private void StartPreviewRefresh(MediaItemViewModel? mediaItem)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        _ = RefreshPreviewAsync(mediaItem, _previewCancellation.Token);
    }

    private async Task RefreshPreviewAsync(
        MediaItemViewModel? mediaItem,
        CancellationToken cancellationToken)
    {
        PreviewImage = null;
        PreviewErrorText = null;

        var video = mediaItem?.Media?.Probe.VideoStreams.FirstOrDefault();
        if (video is null || _frameDecoder is null)
        {
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var duration = mediaItem!.Media!.Probe.Duration;
            var timestamp = duration is null
                ? MediaTime.Zero
                : Min(new MediaTime(1, 1), duration.Value / 10);
            var decodedFrame = await _frameDecoder.DecodeAsync(
                mediaItem.SourcePath,
                video.Index,
                timestamp,
                new PixelSize(1_280, 720),
                cancellationToken);

            await using var stream = new MemoryStream(decodedFrame.EncodedImage.ToArray(), writable: false);
            PreviewImage = new Bitmap(stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A new selection superseded this decode request.
        }
        catch (FrameDecodeException exception)
        {
            PreviewErrorText = exception.Message;
        }
        catch (Exception exception)
        {
            PreviewErrorText = $"Could not display a preview frame: {exception.Message}";
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;
}
