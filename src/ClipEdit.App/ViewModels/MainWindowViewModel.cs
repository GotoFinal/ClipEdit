using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using ClipEdit.Application.Export;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Geometry;
using ClipEdit.Media.Export;
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
    private readonly IExportRenderer? _exportRenderer;
    private readonly SingleSourceExportPlanner _exportPlanner = new();
    private MediaItemViewModel? _selectedMedia;
    private bool _isBusy;
    private bool _isPreviewLoading;
    private Bitmap? _previewImage;
    private string? _previewErrorText;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private ExportPreset _selectedExportPreset = BuiltInExportPresets.Mp4Compatible;
    private bool _isExporting;
    private double _exportProgress;
    private string _exportPhaseText = string.Empty;
    private string _statusText = "Ready";

    public MainWindowViewModel(
        IMediaProbe? mediaProbe,
        IFrameDecoder? frameDecoder = null,
        IExportRenderer? exportRenderer = null)
    {
        _frameDecoder = frameDecoder;
        _exportRenderer = exportRenderer;
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

    public IReadOnlyList<ExportPreset> ExportPresets => BuiltInExportPresets.All;

    public ExportPreset SelectedExportPreset
    {
        get => _selectedExportPreset;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedExportPreset, value))
            {
                RaiseExportStateChanged();
            }
        }
    }

    public MediaItemViewModel? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (ReferenceEquals(_selectedMedia, value))
            {
                return;
            }

            if (_selectedMedia is not null)
            {
                _selectedMedia.PropertyChanged -= OnSelectedMediaPropertyChanged;
            }

            if (SetProperty(ref _selectedMedia, value))
            {
                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedMediaPropertyChanged;
                }

                RaiseWorkspaceStateChanged();
                StartPreviewRefresh(value, debounce: false, clearExisting: true);
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

    public bool IsExportAvailable => _exportRenderer is not null;

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanCancelExport));
                OnPropertyChanged(nameof(ShowExportProgress));
            }
        }
    }

    public bool CanCancelExport => IsExporting;

    public bool ShowExportProgress => IsExporting || ExportProgress > 0;

    public double ExportProgress
    {
        get => _exportProgress;
        private set
        {
            if (SetProperty(ref _exportProgress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ExportProgressPercent));
                OnPropertyChanged(nameof(ShowExportProgress));
            }
        }
    }

    public int ExportProgressPercent => (int)Math.Round(ExportProgress * 100);

    public string ExportPhaseText
    {
        get => _exportPhaseText;
        private set => SetProperty(ref _exportPhaseText, value);
    }

    public bool CanExport => ExportAvailabilityText == "Ready to export" && !IsExporting;

    public string ExportAvailabilityText
    {
        get
        {
            if (_exportRenderer is null)
            {
                return "FFmpeg was not found; export is unavailable";
            }

            if (SelectedMedia?.Media is null || !SelectedMedia.HasVideo)
            {
                return "Select an imported video to export";
            }

            if (VideoItems.Count() > 1)
            {
                return "Multi-video export will be enabled with the sequence timeline";
            }

            if (ExternalAudioItems.Any())
            {
                return "External-audio mixing must be configured before export";
            }

            if (SelectedMedia.Edit is null || SelectedMedia.Edit.IsEmpty)
            {
                return "Keep at least one source range before exporting";
            }

            if (SelectedExportPreset.RequiresEvenDimensions &&
                (((SelectedMedia.Crop.Width & 1) != 0) || ((SelectedMedia.Crop.Height & 1) != 0)))
            {
                return $"{SelectedExportPreset.DisplayName} needs an even crop width and height";
            }

            return "Ready to export";
        }
    }

    public string ExportPlanSummary => SelectedMedia?.Edit is null
        ? SelectedExportPreset.DisplayName
        : $"{SelectedExportPreset.DisplayName} · exact re-encode · " +
          $"{SelectedMedia.CropSizeText} · {SelectedMedia.OutputDurationText}";

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

    public string GetSuggestedExportFileName()
    {
        var sourceName = SelectedMedia is null
            ? "clip"
            : Path.GetFileNameWithoutExtension(SelectedMedia.DisplayName);
        return $"{sourceName}-clip{SelectedExportPreset.FileExtension}";
    }

    public async Task<ExportResult?> ExportAsync(
        string destinationPath,
        bool replaceExistingDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!CanExport || _exportRenderer is null || SelectedMedia?.Media is null || SelectedMedia.Edit is null)
        {
            StatusText = ExportAvailabilityText;
            return null;
        }

        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = _exportCancellation;

        try
        {
            var plan = _exportPlanner.Create(
                SelectedMedia.Media,
                SelectedMedia.Edit,
                SelectedMedia.Crop,
                SelectedExportPreset,
                destinationPath,
                replaceExistingDestination);
            IsExporting = true;
            ExportProgress = 0;
            ExportPhaseText = "Preparing";
            StatusText = ExportPlanSummary;
            var progress = new Progress<ClipEdit.Media.Export.ExportProgress>(update =>
            {
                ExportProgress = update.Fraction;
                ExportPhaseText = $"{update.Phase} · {ExportProgressPercent}%";
            });

            var result = await _exportRenderer.RenderAsync(plan, progress, request.Token);
            ExportProgress = 1;
            ExportPhaseText = "Complete · 100%";
            StatusText = $"Exported {Path.GetFileName(result.DestinationPath)}";
            return result;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            ExportProgress = 0;
            ExportPhaseText = "Canceled";
            StatusText = "Export canceled; the previous destination was not changed";
            return null;
        }
        catch (Exception exception) when (exception is ExportException or ExportPlanException)
        {
            ExportProgress = 0;
            ExportPhaseText = "Failed";
            StatusText = exception.Message;
            return null;
        }
        finally
        {
            if (ReferenceEquals(_exportCancellation, request))
            {
                IsExporting = false;
                request.Dispose();
                _exportCancellation = null;
            }
        }
    }

    public void CancelExport()
    {
        _exportCancellation?.Cancel();
    }

    public void Dispose()
    {
        if (SelectedMedia is not null)
        {
            SelectedMedia.PropertyChanged -= OnSelectedMediaPropertyChanged;
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _exportCancellation = null;
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
        RaiseExportStateChanged();
    }

    private static string BuildAudioStreamText(AudioStreamInfo audio)
    {
        var language = string.IsNullOrWhiteSpace(audio.Language) ? "Audio" : audio.Language.ToUpperInvariant();
        var layout = audio.ChannelLayout ??
                     (audio.ChannelCount is null ? "unknown layout" : $"{audio.ChannelCount} ch");
        return $"{language} · {audio.CodecName.ToUpperInvariant()} · {layout}";
    }

    private void OnSelectedMediaPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MediaItemViewModel.Playhead))
        {
            StartPreviewRefresh((MediaItemViewModel?)sender, debounce: true, clearExisting: false);
        }

        if (eventArgs.PropertyName is nameof(MediaItemViewModel.Crop) or nameof(MediaItemViewModel.Edit))
        {
            RaiseExportStateChanged();
        }
    }

    private void RaiseExportStateChanged()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportAvailabilityText));
        OnPropertyChanged(nameof(ExportPlanSummary));
    }

    private void StartPreviewRefresh(
        MediaItemViewModel? mediaItem,
        bool debounce,
        bool clearExisting)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var request = new CancellationTokenSource();
        _previewCancellation = request;
        _ = RefreshPreviewAsync(mediaItem, request, debounce, clearExisting);
    }

    private async Task RefreshPreviewAsync(
        MediaItemViewModel? mediaItem,
        CancellationTokenSource request,
        bool debounce,
        bool clearExisting)
    {
        var cancellationToken = request.Token;
        if (clearExisting)
        {
            PreviewImage = null;
        }

        PreviewErrorText = null;

        var video = mediaItem?.Media?.Probe.VideoStreams.FirstOrDefault();
        if (video is null || _frameDecoder is null)
        {
            return;
        }

        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(140), cancellationToken);
            }

            IsPreviewLoading = true;
            var decodedFrame = await _frameDecoder.DecodeAsync(
                mediaItem!.SourcePath,
                video.Index,
                mediaItem.Playhead,
                new PixelSize(1_280, 720),
                cancellationToken);

            await using var stream = new MemoryStream(decodedFrame.EncodedImage.ToArray(), writable: false);
            cancellationToken.ThrowIfCancellationRequested();
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
            if (ReferenceEquals(_previewCancellation, request))
            {
                IsPreviewLoading = false;
            }
        }
    }
}
