using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using ClipEdit.Application.Export;
using ClipEdit.Application.Media;
using ClipEdit.Application.Projects;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Analysis;
using ClipEdit.Media.Export;
using ClipEdit.Media.Frames;
using ClipEdit.Media.Probe;
using ClipEdit.Media.Preview;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int SequenceViewportThumbnailCount = 14;
    private static readonly PixelSize TimelineThumbnailSize = new(240, 120);
    private static readonly PixelSize TimelineHoverSize = new(360, 202);
    private static readonly int AnalysisConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly HashSet<string> _knownPaths = new(PathComparer);
    private readonly Dictionary<string, Guid> _pendingMediaIds = new(PathComparer);
    private readonly ImportMediaUseCase? _importMedia;
    private readonly IFrameDecoder? _frameDecoder;
    private readonly IWaveformRenderer? _waveformRenderer;
    private readonly IExportRenderer? _exportRenderer;
    private readonly SingleSourceExportPlanner _exportPlanner = new();
    private readonly IProjectStore? _projectStore;
    private readonly string? _recoveryDirectory;
    private readonly TimeSpan _autosaveDelay;
    private readonly List<ProjectMediaDocument> _unavailableProjectMedia = [];
    private MediaItemViewModel? _selectedMedia;
    private VideoClipViewModel? _selectedVideoClip;
    private bool _isBusy;
    private bool _isPreviewLoading;
    private Bitmap? _previewImage;
    private string? _previewErrorText;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _timelineAnalysisCancellation;
    private CancellationTokenSource? _sequenceTimelineAnalysisCancellation;
    private CancellationTokenSource? _timelineHoverCancellation;
    private Bitmap? _timelineHoverPreviewImage;
    private TimelineFrameCacheKey? _timelineHoverCacheKey;
    private TimelineFrameCacheKey? _timelineHoverRequestKey;
    private double _timelineHoverTime = -1;
    private int _sequenceTimelineVisualRevision;
    private readonly Dictionary<AudioTrackViewModel, CancellationTokenSource> _waveformCancellations = [];
    private readonly SemaphoreSlim _analysisSlots = new(AnalysisConcurrency, AnalysisConcurrency);
    private readonly TimelineFrameCache _timelineFrameCache = new();
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _autosaveCancellation;
    private ExportPreset _selectedExportPreset = BuiltInExportPresets.Mp4Compatible;
    private bool _isExporting;
    private double _exportProgress;
    private string _exportPhaseText = string.Empty;
    private string _statusText = "Ready";
    private Guid _projectId = Guid.NewGuid();
    private string? _projectPath;
    private bool _isProjectDirty;
    private bool _isLoadingProject;
    private bool _isAudioMixerExpanded;
    private CropAspectPreset _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
    private bool _isCropAspectLocked;
    private bool _isApplyingCropPreset;
    private MediaTime _sequencePlayhead;
    private MediaTime _sequenceSelectionStart;
    private MediaTime _sequenceSelectionEnd;
    private double _sequenceTimelineZoom = 1;
    private double _sequenceTimelineViewportStart;

    public MainWindowViewModel(
        IMediaProbe? mediaProbe,
        IFrameDecoder? frameDecoder = null,
        IExportRenderer? exportRenderer = null,
        IProjectStore? projectStore = null,
        string? recoveryDirectory = null,
        TimeSpan? autosaveDelay = null,
        IWaveformRenderer? waveformRenderer = null)
    {
        _frameDecoder = frameDecoder;
        _waveformRenderer = waveformRenderer;
        _exportRenderer = exportRenderer;
        _projectStore = projectStore;
        _recoveryDirectory = recoveryDirectory;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(5);
        if (_autosaveDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autosaveDelay));
        }
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

    public string WorkspaceTitle =>
        $"{(ShowTimeline ? "Timeline edit" : "Create a short clip")} · {ProjectDisplayName}" +
        (IsProjectDirty ? " *" : string.Empty);

    public string EmptyStateTitle => "Drop a video to begin";

    public string EmptyStateDescription =>
        "Your source stays untouched. ClipEdit will reveal trimming and crop controls after import.";

    public string SupportedMediaHint => "Video and audio files supported by the local media engine";

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = [];

    public ObservableCollection<VideoClipViewModel> VideoClips { get; } = [];

    public ObservableCollection<AudioTrackViewModel> AudioTracks { get; } = [];

    public bool IsProjectPersistenceAvailable => _projectStore is not null;

    public string? ProjectPath
    {
        get => _projectPath;
        private set
        {
            if (SetProperty(ref _projectPath, value))
            {
                OnPropertyChanged(nameof(ProjectDisplayName));
                OnPropertyChanged(nameof(WorkspaceTitle));
            }
        }
    }

    public string ProjectDisplayName => ProjectPath is null
        ? "Untitled project"
        : Path.GetFileNameWithoutExtension(ProjectPath);

    public bool IsProjectDirty
    {
        get => _isProjectDirty;
        private set
        {
            if (SetProperty(ref _isProjectDirty, value))
            {
                OnPropertyChanged(nameof(WorkspaceTitle));
                OnPropertyChanged(nameof(CanSaveProject));
                OnPropertyChanged(nameof(CanOpenProject));
            }
        }
    }

    public bool CanSaveProject =>
        IsProjectPersistenceAvailable && HasReadyMedia && !IsBusy && !IsExporting;

    public bool CanOpenProject =>
        IsProjectPersistenceAvailable && !IsBusy && !IsExporting && !IsProjectDirty;

    public bool CanNewProject =>
        !IsBusy && !IsExporting && (MediaItems.Count > 0 || ProjectPath is not null || IsProjectDirty);

    public bool CanRemoveSelectedMedia => SelectedMedia is not null && !IsBusy && !IsExporting;

    public IReadOnlyList<CropAspectPreset> CropAspectPresets => BuiltInCropAspectPresets.All;

    public CropAspectPreset SelectedCropAspectPreset
    {
        get => _selectedCropAspectPreset;
        set
        {
            var next = value ?? BuiltInCropAspectPresets.Custom;
            if (SetProperty(ref _selectedCropAspectPreset, next) && !next.IsCustom)
            {
                ApplySelectedCropPreset();
            }
        }
    }

    public bool IsCropAspectLocked
    {
        get => _isCropAspectLocked;
        set
        {
            if (SetProperty(ref _isCropAspectLocked, value))
            {
                StatusText = value
                    ? "Crop aspect locked; handles only change scale. Shift or Ctrl also locks while dragging."
                    : "Crop aspect unlocked; resizing manually switches the preset to Custom.";
                MarkProjectDirty();
            }
        }
    }

    public bool CanApplyCropPreset => SelectedVideoClip is not null;

    public bool CanApplyCropPresetToAll => VideoClips.Count > 1;

    public bool CanMoveSelectedVideoLeft =>
        SelectedVideoClip is { } clip && VideoClips.IndexOf(clip) > 0;

    public bool CanMoveSelectedVideoRight
    {
        get
        {
            var index = SelectedVideoClip is null ? -1 : VideoClips.IndexOf(SelectedVideoClip);
            return index >= 0 && index < VideoClips.Count - 1;
        }
    }

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
                MarkProjectDirty();
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
                _selectedMedia.SetTimelineThumbnails([]);
            }

            if (SetProperty(ref _selectedMedia, value))
            {
                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedMediaPropertyChanged;
                }

                RaiseWorkspaceStateChanged();
                StartPreviewRefresh(value, debounce: false, clearExisting: true);
                OnPropertyChanged(nameof(CanRemoveSelectedMedia));
                OnPropertyChanged(nameof(CanApplyCropPreset));
                OnPropertyChanged(nameof(CanMoveSelectedVideoLeft));
                OnPropertyChanged(nameof(CanMoveSelectedVideoRight));

                var matchingClip = value is { HasVideo: true }
                    ? VideoClips.FirstOrDefault(clip => ReferenceEquals(clip.Source, value))
                    : null;
                if (!ReferenceEquals(SelectedVideoClip?.Source, value))
                {
                    SelectedVideoClip = matchingClip;
                }
            }
        }
    }

    public VideoClipViewModel? SelectedVideoClip
    {
        get => _selectedVideoClip;
        set
        {
            if (!SetProperty(ref _selectedVideoClip, value))
            {
                return;
            }

            if (value is not null && !ReferenceEquals(SelectedMedia, value.Source))
            {
                SelectedMedia = value.Source;
            }

            OnPropertyChanged(nameof(CanDeleteSelectedVideoClip));
            OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
            OnPropertyChanged(nameof(CanApplyCropPreset));
            OnPropertyChanged(nameof(CanMoveSelectedVideoLeft));
            OnPropertyChanged(nameof(CanMoveSelectedVideoRight));
            RaiseExportStateChanged();
        }
    }

    public double SequenceDurationSeconds =>
        VideoClips.Aggregate(0d, static (total, clip) => total + clip.DurationSeconds);

    public double SequencePlayheadSeconds
    {
        get => _sequencePlayhead.TotalSeconds;
        set
        {
            var next = SequenceTimeFromSeconds(value);
            if (!SetProperty(ref _sequencePlayhead, next, nameof(SequencePlayheadSeconds)))
            {
                return;
            }

            OnPropertyChanged(nameof(SequencePlayheadText));
            OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
            SyncSourcePreviewToSequenceTime(next, selectClip: true);
        }
    }

    public string SequencePlayheadText => FormatSequenceTimestamp(_sequencePlayhead);

    public double SequenceSelectionStartSeconds
    {
        get => _sequenceSelectionStart.TotalSeconds;
        set
        {
            var next = SequenceTimeFromSeconds(value);
            if (SetProperty(ref _sequenceSelectionStart, next, nameof(SequenceSelectionStartSeconds)))
            {
                RaiseSequenceSelectionChanged();
            }
        }
    }

    public double SequenceSelectionEndSeconds
    {
        get => _sequenceSelectionEnd.TotalSeconds;
        set
        {
            var next = SequenceTimeFromSeconds(value);
            if (SetProperty(ref _sequenceSelectionEnd, next, nameof(SequenceSelectionEndSeconds)))
            {
                RaiseSequenceSelectionChanged();
            }
        }
    }

    public string SequenceSelectionRangeText =>
        $"{FormatSequenceTimestamp(Min(_sequenceSelectionStart, _sequenceSelectionEnd))} – " +
        FormatSequenceTimestamp(Max(_sequenceSelectionStart, _sequenceSelectionEnd));

    public string SequenceOutputDurationText =>
        $"Output {FormatSequenceTimestamp(new MediaTime(checked((long)Math.Round(SequenceDurationSeconds * 1_000)), 1_000))}";

    public string SequenceSelectedDurationText =>
        $"Selected {FormatSequenceTimestamp(Max(_sequenceSelectionStart, _sequenceSelectionEnd) - Min(_sequenceSelectionStart, _sequenceSelectionEnd))}";

    public bool HasSequenceSelection => _sequenceSelectionEnd > _sequenceSelectionStart;

    public bool CanRemoveSequenceSelection => HasSequenceSelection && VideoClips.Count > 0;

    public bool CanKeepSequenceSelection =>
        HasSequenceSelection &&
        (_sequenceSelectionStart > MediaTime.Zero ||
         _sequenceSelectionEnd < SequenceTimeFromSeconds(SequenceDurationSeconds));

    public bool CanDeleteSelectedVideoClip => SelectedVideoClip is not null && !IsBusy && !IsExporting;

    public bool CanSplitSelectedVideoClip =>
        SelectedVideoClip is { } clip &&
        _sequencePlayhead > clip.TimelineStart &&
        _sequencePlayhead < clip.TimelineEnd;

    public double SequenceTimelineZoom
    {
        get => _sequenceTimelineZoom;
        set
        {
            var next = TimelineViewportMath.ClampZoom(value);
            if (!SetProperty(ref _sequenceTimelineZoom, next))
            {
                return;
            }

            SequenceTimelineViewportStart = _sequenceTimelineViewportStart;
            RaiseSequenceViewportChanged();
            StartSequenceTimelineAnalysis(debounce: true);
        }
    }

    public double SequenceTimelineViewportStart
    {
        get => _sequenceTimelineViewportStart;
        set
        {
            var next = TimelineViewportMath.ClampStart(SequenceDurationSeconds, SequenceTimelineZoom, value);
            if (SetProperty(ref _sequenceTimelineViewportStart, next))
            {
                RaiseSequenceViewportChanged();
                StartSequenceTimelineAnalysis(debounce: true);
            }
        }
    }

    public double SequenceTimelineViewportDuration =>
        TimelineViewportMath.VisibleDuration(SequenceDurationSeconds, SequenceTimelineZoom);

    public double SequenceTimelineViewportEnd =>
        Math.Min(SequenceDurationSeconds, SequenceTimelineViewportStart + SequenceTimelineViewportDuration);

    public string SequenceTimelineZoomText => $"{SequenceTimelineZoom:0.#}×";

    public string SequenceTimelineViewportText =>
        $"{FormatSequenceTimestamp(SequenceTimeFromSeconds(SequenceTimelineViewportStart))} – " +
        FormatSequenceTimestamp(SequenceTimeFromSeconds(SequenceTimelineViewportEnd));

    public bool CanZoomSequenceTimelineIn => SequenceTimelineZoom < TimelineViewportMath.MaximumZoom;

    public bool CanZoomSequenceTimelineOut => SequenceTimelineZoom > 1;

    public double TimelineHoverTime
    {
        get => _timelineHoverTime;
        set
        {
            var next = double.IsFinite(value) && value >= 0
                ? Math.Clamp(value, 0, SequenceDurationSeconds)
                : -1;
            if (!SetProperty(ref _timelineHoverTime, next))
            {
                return;
            }

            OnPropertyChanged(nameof(HasTimelineHoverPreview));
            StartTimelineHoverPreview(next);
        }
    }

    public Bitmap? TimelineHoverPreviewImage
    {
        get => _timelineHoverPreviewImage;
        private set
        {
            var previous = _timelineHoverPreviewImage;
            if (SetProperty(ref _timelineHoverPreviewImage, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasTimelineHoverPreview));
            }
        }
    }

    public bool HasTimelineHoverPreview => TimelineHoverTime >= 0 && TimelineHoverPreviewImage is not null;

    public int SequenceTimelineVisualRevision => _sequenceTimelineVisualRevision;

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
                OnPropertyChanged(nameof(CanSaveProject));
                OnPropertyChanged(nameof(CanOpenProject));
                OnPropertyChanged(nameof(CanNewProject));
                OnPropertyChanged(nameof(CanRemoveSelectedMedia));
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

            var slices = GetSequenceExportSlices();
            if (slices.Count == 0)
            {
                return "The timeline selection contains no video";
            }

            var outputSize = slices[0].Clip.SourceWindow.ExportSize;
            if (SelectedExportPreset.RequiresEvenDimensions &&
                (((outputSize.Width & 1) != 0) || ((outputSize.Height & 1) != 0)))
            {
                return $"{SelectedExportPreset.DisplayName} requires even output dimensions; current crop is {outputSize.Width} × {outputSize.Height}";
            }

            return "Ready to export";
        }
    }

    public string ExportPlanSummary
    {
        get
        {
            var slices = GetSequenceExportSlices();
            if (slices.Count == 0)
            {
                return SelectedExportPreset.DisplayName;
            }

            var outputSize = slices[0].Clip.SourceWindow.ExportSize;
            var duration = slices.Aggregate(
                MediaTime.Zero,
                static (total, slice) => total + slice.SourceRange.Duration);
            return $"{SelectedExportPreset.DisplayName} · exact sequence re-encode · " +
                   $"{outputSize.Width} × {outputSize.Height} · {FormatSequenceTimestamp(duration)}";
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSaveProject));
                OnPropertyChanged(nameof(CanOpenProject));
                OnPropertyChanged(nameof(CanNewProject));
                OnPropertyChanged(nameof(CanRemoveSelectedMedia));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasReadyMedia => MediaItems.Any(item => item.IsReady);

    public bool ShowEmptyState => !HasReadyMedia;

    public bool ShowQuickWorkspace => HasReadyMedia;

    public bool ShowTimeline => VideoClips.Count > 0;

    public bool HasAudioTracks => AudioTracks.Count > 0;

    public bool ShowAudioMixer =>
        HasAudioTracks && (_isAudioMixerExpanded || ExternalAudioItems.Any());

    public bool ShowRangeStrip => false;

    public IEnumerable<MediaItemViewModel> VideoItems => MediaItems.Where(item => item.HasVideo);

    public IEnumerable<MediaItemViewModel> ExternalAudioItems => MediaItems.Where(item => item.IsExternalAudio);

    public string AudioTrackCountText =>
        $"{AudioTracks.Count} track{(AudioTracks.Count == 1 ? string.Empty : "s")}";

    public IReadOnlyList<PreviewAudioTrack> PreviewAudioTracks => SelectedMedia is null
        ? []
        : AudioTracks
            .Where(track =>
                track.IsExternal ||
                PathComparer.Equals(track.SourcePath, SelectedMedia.SourcePath))
            .Select(track => track.IsExternal
                ? new PreviewAudioTrack(
                    track.SourcePath,
                    track.StreamIndex,
                    track.GainDb,
                    track.IsMuted || track.Edit.IsEmpty,
                    track.TimelineOffset,
                    track.Edit)
                : new PreviewAudioTrack(
                    track.StreamIndex,
                    track.GainDb,
                    track.IsMuted || track.Edit.IsEmpty,
                    track.Edit))
            .ToArray();

    public string AudioMixerButtonText => ShowAudioMixer ? "Hide mixer" : "Mixer";

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

            var item = new MediaItemViewModel(
                fullPath,
                _pendingMediaIds.GetValueOrDefault(fullPath) is { } savedId && savedId != Guid.Empty
                    ? savedId
                    : null);
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
            AddAudioTracks(item);
            if (item is { IsReady: true, HasVideo: true })
            {
                AddInitialVideoClip(item);
            }
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
        if (readyCount > 0)
        {
            MarkProjectDirty();
        }

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
        var slices = GetSequenceExportSlices();
        if (!CanExport || _exportRenderer is null || slices.Count == 0)
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
            var videoSegments = slices.Select(slice =>
            {
                var video = slice.Clip.Source.Media!.Probe.VideoStreams.First();
                var embeddedAudio = AudioTracks
                    .Where(track =>
                        !track.IsExternal &&
                        !track.IsMuted &&
                        !track.Edit.IsEmpty &&
                        PathComparer.Equals(track.SourcePath, slice.Clip.SourcePath))
                    .Select(track => new ExportAudioTrackPlan(
                        track.StreamIndex,
                        track.GainDb,
                        track.Edit))
                    .ToImmutableArray();
                return new ExportVideoSegmentPlan(
                    slice.Clip.SourcePath,
                    video.Index,
                    slice.SourceRange,
                    slice.Clip.SourceWindow,
                    embeddedAudio);
            }).ToImmutableArray();
            var externalAudio = AudioTracks
                .Where(track => track.IsExternal && !track.IsMuted && !track.Edit.IsEmpty)
                .Select(track => new ExportAudioTrackPlan(
                    track.SourcePath,
                    track.StreamIndex,
                    track.GainDb,
                    track.TimelineOffset,
                    track.Edit))
                .ToImmutableArray();
            var selectionStart = HasSequenceSelection
                ? NormalizedSequenceSelection().Start
                : MediaTime.Zero;
            var plan = new ExportPlan(
                videoSegments,
                slices[0].Clip.SourceWindow.ExportSize,
                destinationPath,
                SelectedExportPreset,
                replaceExistingDestination,
                externalAudio,
                selectionStart);
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

    public void ToggleAudioMixer()
    {
        _isAudioMixerExpanded = !_isAudioMixerExpanded;
        OnPropertyChanged(nameof(ShowAudioMixer));
        OnPropertyChanged(nameof(AudioMixerButtonText));
        if (ShowAudioMixer)
        {
            foreach (var track in AudioTracks)
            {
                StartWaveformAnalysis(track, debounce: false);
            }
        }
    }

    public void MarkSequenceSelectionStart()
    {
        _sequenceSelectionStart = _sequencePlayhead;
        if (_sequenceSelectionEnd < _sequenceSelectionStart)
        {
            _sequenceSelectionEnd = _sequenceSelectionStart;
        }

        OnPropertyChanged(nameof(SequenceSelectionStartSeconds));
        OnPropertyChanged(nameof(SequenceSelectionEndSeconds));
        RaiseSequenceSelectionChanged();
    }

    public void MarkSequenceSelectionEnd()
    {
        _sequenceSelectionEnd = _sequencePlayhead;
        if (_sequenceSelectionStart > _sequenceSelectionEnd)
        {
            _sequenceSelectionStart = _sequenceSelectionEnd;
        }

        OnPropertyChanged(nameof(SequenceSelectionStartSeconds));
        OnPropertyChanged(nameof(SequenceSelectionEndSeconds));
        RaiseSequenceSelectionChanged();
    }

    public bool RemoveSequenceSelection()
    {
        var selection = NormalizedSequenceSelection();
        if (selection.IsEmpty || VideoClips.Count == 0)
        {
            return false;
        }

        var replacements = new List<VideoClipViewModel>(VideoClips.Count + 1);
        foreach (var clip in VideoClips)
        {
            var overlapStart = Max(selection.Start, clip.TimelineStart);
            var overlapEnd = Min(selection.End, clip.TimelineEnd);
            if (overlapEnd <= overlapStart)
            {
                replacements.Add(clip);
                continue;
            }

            var sourceRemoval = new MediaRange(
                clip.SourceStart + (overlapStart - clip.TimelineStart),
                clip.SourceStart + (overlapEnd - clip.TimelineStart));
            foreach (var part in clip.Model.Remove(sourceRemoval, Guid.NewGuid()))
            {
                replacements.Add(clip.CreateSibling(part));
            }
        }

        ReplaceVideoClips(replacements, preferredClipId: null);
        CollapseSequenceSelection(selection.Start);
        StatusText = "Removed the selected timeline range; affected clips were split and source handles remain recoverable";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool KeepSequenceSelectionOnly()
    {
        var selection = NormalizedSequenceSelection();
        if (selection.IsEmpty || VideoClips.Count == 0)
        {
            return false;
        }

        var replacements = new List<VideoClipViewModel>(VideoClips.Count);
        foreach (var clip in VideoClips)
        {
            var overlapStart = Max(selection.Start, clip.TimelineStart);
            var overlapEnd = Min(selection.End, clip.TimelineEnd);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            var sourceSelection = new MediaRange(
                clip.SourceStart + (overlapStart - clip.TimelineStart),
                clip.SourceStart + (overlapEnd - clip.TimelineStart));
            if (clip.Model.KeepOnly(sourceSelection) is { } kept)
            {
                replacements.Add(clip.CreateSibling(kept));
            }
        }

        ReplaceVideoClips(replacements, preferredClipId: null);
        _sequencePlayhead = MediaTime.Zero;
        _sequenceSelectionStart = MediaTime.Zero;
        _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
        RaiseSequenceStateChanged();
        SyncSourcePreviewToSequenceTime(MediaTime.Zero, selectClip: true);
        StatusText = "Kept only the selected timeline section; drag either clip edge outward to restore hidden source";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool SplitSelectedVideoClip()
    {
        var clip = FindClipAtTimelineTime(_sequencePlayhead) ?? SelectedVideoClip;
        if (clip is null || _sequencePlayhead <= clip.TimelineStart || _sequencePlayhead >= clip.TimelineEnd)
        {
            return false;
        }

        var sourceTime = clip.SourceStart + (_sequencePlayhead - clip.TimelineStart);
        var (left, right) = clip.Model.Split(sourceTime, Guid.NewGuid());
        var index = VideoClips.IndexOf(clip);
        var replacements = VideoClips.ToList();
        replacements.RemoveAt(index);
        replacements.Insert(index, clip.CreateSibling(left));
        replacements.Insert(index + 1, clip.CreateSibling(right));
        ReplaceVideoClips(replacements, right.Id);
        CollapseSequenceSelection(_sequencePlayhead);
        StatusText = $"Split {clip.DisplayName} at {SequencePlayheadText}";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool DeleteSelectedVideoClip()
    {
        if (SelectedVideoClip is not { } clip || IsBusy || IsExporting)
        {
            return false;
        }

        var index = VideoClips.IndexOf(clip);
        var replacements = VideoClips.Where(candidate => !ReferenceEquals(candidate, clip)).ToList();
        var preferred = replacements.Count == 0
            ? (Guid?)null
            : replacements[Math.Min(index, replacements.Count - 1)].Id;
        ReplaceVideoClips(replacements, preferred);
        CollapseSequenceSelection(SequenceTimeFromSeconds(Math.Min(SequencePlayheadSeconds, SequenceDurationSeconds)));
        StatusText = $"Removed {clip.DisplayName} from the timeline; its source remains in Media";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool ResetSequenceCuts()
    {
        var videoSources = MediaItems.Where(item => item is { IsReady: true, HasVideo: true }).ToArray();
        if (videoSources.Length == 0)
        {
            return false;
        }

        var placements = VideoClips
            .GroupBy(clip => clip.Source.Id)
            .ToDictionary(group => group.Key, group => group.First().SourceWindow);
        var replacements = new List<VideoClipViewModel>(videoSources.Length);
        foreach (var source in videoSources)
        {
            var duration = source.Edit?.SourceDuration ?? source.Media?.Probe.Duration;
            if (duration is null || duration <= MediaTime.Zero)
            {
                continue;
            }

            var fullRange = new MediaRange(MediaTime.Zero, duration.Value);
            replacements.Add(new VideoClipViewModel(
                source,
                new SequenceClip(Guid.NewGuid(), source.Id, fullRange, fullRange),
                placements.GetValueOrDefault(source.Id, source.Crop)));
        }

        ReplaceVideoClips(replacements, replacements.FirstOrDefault()?.Id);
        _sequencePlayhead = MediaTime.Zero;
        _sequenceSelectionStart = MediaTime.Zero;
        _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
        SequenceTimelineZoom = 1;
        RaiseSequenceStateChanged();
        SyncSourcePreviewToSequenceTime(MediaTime.Zero, selectClip: true);
        StatusText = "Restored each video source as one full timeline clip";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public void ZoomSequenceTimeline(double factor, double? anchor = null)
    {
        if (!double.IsFinite(factor) || factor <= 0 || SequenceDurationSeconds <= 0)
        {
            return;
        }

        var result = TimelineViewportMath.ZoomAround(
            SequenceDurationSeconds,
            SequenceTimelineZoom,
            SequenceTimelineViewportStart,
            SequenceTimelineZoom * factor,
            anchor ?? SequencePlayheadSeconds);
        _sequenceTimelineZoom = result.Zoom;
        _sequenceTimelineViewportStart = result.Start;
        RaiseSequenceViewportChanged();
        StartSequenceTimelineAnalysis(debounce: true);
    }

    public void FitSequenceTimeline()
    {
        _sequenceTimelineZoom = 1;
        _sequenceTimelineViewportStart = 0;
        RaiseSequenceViewportChanged();
        StartSequenceTimelineAnalysis(debounce: false);
    }

    public bool ApplyCropPresetToSelected()
    {
        return ApplySelectedCropPreset();
    }

    public bool ApplyCropPresetToAllVideos()
    {
        return ApplySelectedCropPreset();
    }

    public bool ResetSelectedClipPlacement()
    {
        if (SelectedVideoClip is not { } clip)
        {
            return false;
        }

        _isApplyingCropPreset = true;
        try
        {
            clip.SourceWindow = SelectedCropAspectPreset switch
            {
                { IsCustom: false, IsFullFrame: false } preset =>
                    CropRegion.FullFrame(clip.VideoSize).ResizeToAspectRatio(
                        preset.WidthUnits,
                        preset.HeightUnits),
                _ => CropRegion.FullFrame(clip.VideoSize),
            };
        }
        finally
        {
            _isApplyingCropPreset = false;
        }

        StatusText = $"Centered {clip.DisplayName} under the shared crop frame";
        MarkProjectDirty();
        return true;
    }

    private bool ApplySelectedCropPreset()
    {
        if (SelectedCropAspectPreset.IsCustom || VideoClips.Count == 0)
        {
            return false;
        }

        _isApplyingCropPreset = true;
        try
        {
            foreach (var clip in VideoClips)
            {
                clip.SourceWindow = SelectedCropAspectPreset.IsFullFrame
                    ? CropRegion.FullFrame(clip.VideoSize)
                    : clip.SourceWindow.ResizeToAspectRatio(
                        SelectedCropAspectPreset.WidthUnits,
                        SelectedCropAspectPreset.HeightUnits);
            }
        }
        finally
        {
            _isApplyingCropPreset = false;
        }

        StatusText = $"Applied {SelectedCropAspectPreset.DisplayName} to the shared crop frame; each clip keeps its own position underneath";
        MarkProjectDirty();
        RaiseExportStateChanged();
        return true;
    }

    public bool MoveSelectedVideoLeft()
    {
        var index = SelectedVideoClip is null ? -1 : VideoClips.IndexOf(SelectedVideoClip);
        return index > 0 && ReorderVideoClip(
            VideoClips[index],
            VideoClips[index - 1],
            insertAfterTarget: false);
    }

    public bool MoveSelectedVideoRight()
    {
        var index = SelectedVideoClip is null ? -1 : VideoClips.IndexOf(SelectedVideoClip);
        return index >= 0 && index < VideoClips.Count - 1 &&
               ReorderVideoClip(VideoClips[index], VideoClips[index + 1], insertAfterTarget: true);
    }

    public bool ReorderVideoClip(
        VideoClipViewModel source,
        VideoClipViewModel target,
        bool insertAfterTarget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target))
        {
            return false;
        }

        var sourceIndex = VideoClips.IndexOf(source);
        var targetIndex = VideoClips.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return false;
        }

        var insertionIndex = targetIndex + (insertAfterTarget ? 1 : 0);
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        var destinationIndex = Math.Clamp(insertionIndex, 0, VideoClips.Count - 1);
        if (destinationIndex == sourceIndex)
        {
            return false;
        }

        VideoClips.Move(sourceIndex, destinationIndex);
        UpdateSequenceLayout(resetSelectionIfEmpty: false);
        SelectedVideoClip = source;
        StatusText = $"Moved {source.DisplayName} in the video sequence";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool ReorderVideoClip(
        MediaItemViewModel source,
        MediaItemViewModel target,
        bool insertAfterTarget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target) || !source.HasVideo || !target.HasVideo)
        {
            return false;
        }

        var sourceIndex = MediaItems.IndexOf(source);
        var targetIndex = MediaItems.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return false;
        }

        var insertionIndex = targetIndex + (insertAfterTarget ? 1 : 0);
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        var destinationIndex = Math.Clamp(insertionIndex, 0, MediaItems.Count - 1);
        if (destinationIndex == sourceIndex)
        {
            return false;
        }

        MediaItems.Move(sourceIndex, destinationIndex);
        SelectedMedia = source;
        StatusText = $"Moved {source.DisplayName} in the video sequence";
        MarkProjectDirty();
        RaiseWorkspaceStateChanged();
        return true;
    }

    public async Task<bool> NewProjectAsync(
        bool discardUnsavedChanges = false,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy || IsExporting)
        {
            StatusText = "Wait for the current operation before creating a new project.";
            return false;
        }

        if (IsProjectDirty && !discardUnsavedChanges)
        {
            StatusText = "Confirm that you want to discard the current project's unsaved changes.";
            return false;
        }

        _isLoadingProject = true;
        try
        {
            await DeleteRecoveryAsync(cancellationToken);
            ClearProjectContent();
            _projectId = Guid.NewGuid();
            ProjectPath = null;
            SelectedExportPreset = BuiltInExportPresets.Mp4Compatible;
            _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
            _isCropAspectLocked = false;
            OnPropertyChanged(nameof(SelectedCropAspectPreset));
            OnPropertyChanged(nameof(IsCropAspectLocked));
            IsProjectDirty = false;
            ExportProgress = 0;
            ExportPhaseText = string.Empty;
            StatusText = "New project ready";
            RaiseWorkspaceStateChanged();
            return true;
        }
        finally
        {
            _isLoadingProject = false;
        }
    }

    public bool RemoveSelectedMedia()
    {
        if (SelectedMedia is not { } mediaItem || IsBusy || IsExporting)
        {
            return false;
        }

        var selectedIndex = MediaItems.IndexOf(mediaItem);
        SelectedMedia = null;
        foreach (var clip in VideoClips
                     .Where(clip => ReferenceEquals(clip.Source, mediaItem))
                     .ToArray())
        {
            DetachVideoClip(clip);
            clip.Dispose();
            VideoClips.Remove(clip);
        }

        UpdateSequenceLayout(resetSelectionIfEmpty: true);
        foreach (var audioTrack in AudioTracks
                     .Where(track => PathComparer.Equals(track.SourcePath, mediaItem.SourcePath))
                     .ToArray())
        {
            audioTrack.PropertyChanged -= OnAudioTrackPropertyChanged;
            CancelWaveformAnalysis(audioTrack);
            audioTrack.Dispose();
            AudioTracks.Remove(audioTrack);
        }

        MediaItems.Remove(mediaItem);
        mediaItem.Dispose();
        _knownPaths.Remove(mediaItem.SourcePath);
        _unavailableProjectMedia.RemoveAll(saved =>
            PathComparer.Equals(saved.SourcePath, mediaItem.SourcePath));
        SelectedMedia = MediaItems.Count == 0
            ? null
            : MediaItems[Math.Min(selectedIndex, MediaItems.Count - 1)];
        StatusText = $"Removed {mediaItem.DisplayName} from the project; the source file was not changed";
        MarkProjectDirty();
        RaiseWorkspaceStateChanged();
        return true;
    }

    public async Task<bool> SaveProjectAsync(
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
        if (_projectStore is null)
        {
            StatusText = "Project saving is unavailable.";
            return false;
        }

        var destination = projectPath ?? ProjectPath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            StatusText = "Choose a project filename first.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(destination);
            await _projectStore.SaveAsync(fullPath, CreateProjectDocument(), cancellationToken);
            ProjectPath = fullPath;
            IsProjectDirty = false;
            StatusText = $"Saved {Path.GetFileName(fullPath)}";
            await DeleteRecoveryAsync(cancellationToken);
            return true;
        }
        catch (ProjectStoreException exception)
        {
            StatusText = exception.Message;
            return false;
        }
    }

    public async Task<bool> OpenProjectAsync(
        string projectPath,
        bool discardUnsavedChanges = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (_projectStore is null)
        {
            StatusText = "Project opening is unavailable.";
            return false;
        }

        if (IsProjectDirty && !discardUnsavedChanges)
        {
            StatusText = "Save the current project before opening another one.";
            return false;
        }

        ProjectDocument document;
        try
        {
            document = await _projectStore.LoadAsync(projectPath, cancellationToken);
        }
        catch (ProjectStoreException exception)
        {
            StatusText = exception.Message;
            return false;
        }

        _isLoadingProject = true;
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = null;
        try
        {
            SelectedMedia = null;
            SelectedVideoClip = null;
            foreach (var clip in VideoClips)
            {
                DetachVideoClip(clip);
                clip.Dispose();
            }

            VideoClips.Clear();
            foreach (var audioTrack in AudioTracks)
            {
                audioTrack.PropertyChanged -= OnAudioTrackPropertyChanged;
                CancelWaveformAnalysis(audioTrack);
                audioTrack.Dispose();
            }

            foreach (var mediaItem in MediaItems)
            {
                mediaItem.Dispose();
            }

            AudioTracks.Clear();
            MediaItems.Clear();
            _knownPaths.Clear();
            _unavailableProjectMedia.Clear();
            _projectId = document.ProjectId;
            SelectedExportPreset = ExportPresets.FirstOrDefault(
                                       preset => preset.Id == document.ExportPresetId) ??
                                   BuiltInExportPresets.Mp4Compatible;

            foreach (var savedMedia in document.Media)
            {
                if (savedMedia.MediaId != Guid.Empty)
                {
                    _pendingMediaIds[Path.GetFullPath(savedMedia.SourcePath)] = savedMedia.MediaId;
                }
            }

            await ImportFilesAsync(document.Media.Select(media => media.SourcePath), cancellationToken);
            _pendingMediaIds.Clear();
            var warnings = new List<string>();
            foreach (var savedMedia in document.Media)
            {
                var mediaItem = MediaItems.FirstOrDefault(item =>
                    PathComparer.Equals(item.SourcePath, Path.GetFullPath(savedMedia.SourcePath)));
                if (mediaItem is null || !mediaItem.IsReady)
                {
                    _unavailableProjectMedia.Add(savedMedia);
                    warnings.Add($"{Path.GetFileName(savedMedia.SourcePath)} is unavailable");
                    continue;
                }

                if (!TryRestoreMedia(mediaItem, savedMedia, out var warning))
                {
                    _unavailableProjectMedia.Add(savedMedia);
                    warnings.Add(warning!);
                }
            }

            RestoreVideoSequence(document, warnings);

            ProjectPath = Path.GetFullPath(projectPath);
            IsProjectDirty = false;
            StatusText = warnings.Count == 0
                ? $"Opened {Path.GetFileName(ProjectPath)}"
                : $"Opened with {warnings.Count} warning{(warnings.Count == 1 ? string.Empty : "s")}: {warnings[0]}";
            RaiseWorkspaceStateChanged();
            return true;
        }
        finally
        {
            _isLoadingProject = false;
        }
    }

    public async Task<bool> RecoverProjectAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        var recovered = await OpenProjectAsync(
            recoveryPath,
            discardUnsavedChanges: true,
            cancellationToken);
        if (!recovered)
        {
            return false;
        }

        ProjectPath = null;
        IsProjectDirty = true;
        StatusText = "Recovered autosaved edits from the previous session; save the project to keep them";
        ScheduleAutosave();
        return true;
    }

    public ProjectDocument CreateProjectDocument()
    {
        var mediaDocuments = new List<ProjectMediaDocument>(MediaItems.Count);
        foreach (var item in MediaItems)
        {
            var preserved = _unavailableProjectMedia.FirstOrDefault(saved =>
                PathComparer.Equals(saved.SourcePath, item.SourcePath));
            if (preserved is not null)
            {
                mediaDocuments.Add(preserved);
                continue;
            }

            if (TryCreateMediaDocument(item, out var mediaDocument))
            {
                mediaDocuments.Add(mediaDocument!);
            }
        }

        foreach (var preserved in _unavailableProjectMedia)
        {
            if (!mediaDocuments.Any(media => PathComparer.Equals(media.SourcePath, preserved.SourcePath)))
            {
                mediaDocuments.Add(preserved);
            }
        }

        return new ProjectDocument(
            ProjectDocument.CurrentSchemaVersion,
            _projectId,
            SelectedExportPreset.Id,
            mediaDocuments,
            VideoClips.Select(CreateVideoClipDocument).ToArray(),
            new ProjectCropSettingsDocument(SelectedCropAspectPreset.Id, IsCropAspectLocked));
    }

    public void Dispose()
    {
        if (SelectedMedia is not null)
        {
            SelectedMedia.PropertyChanged -= OnSelectedMediaPropertyChanged;
        }

        foreach (var clip in VideoClips)
        {
            DetachVideoClip(clip);
            clip.Dispose();
        }

        foreach (var audioTrack in AudioTracks)
        {
            audioTrack.PropertyChanged -= OnAudioTrackPropertyChanged;
            CancelWaveformAnalysis(audioTrack);
            audioTrack.Dispose();
        }

        foreach (var mediaItem in MediaItems)
        {
            mediaItem.Dispose();
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _timelineAnalysisCancellation?.Cancel();
        _timelineAnalysisCancellation = null;
        _sequenceTimelineAnalysisCancellation?.Cancel();
        _sequenceTimelineAnalysisCancellation = null;
        _timelineHoverCancellation?.Cancel();
        _timelineHoverCancellation = null;
        _timelineHoverRequestKey = null;
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _exportCancellation = null;
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = null;
        PreviewImage = null;
        TimelineHoverPreviewImage = null;
    }

    private void RaiseWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasReadyMedia));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowQuickWorkspace));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(ShowAudioMixer));
        OnPropertyChanged(nameof(HasAudioTracks));
        OnPropertyChanged(nameof(AudioMixerButtonText));
        OnPropertyChanged(nameof(ShowRangeStrip));
        OnPropertyChanged(nameof(VideoItems));
        OnPropertyChanged(nameof(ExternalAudioItems));
        OnPropertyChanged(nameof(AudioTrackCountText));
        OnPropertyChanged(nameof(PreviewAudioTracks));
        OnPropertyChanged(nameof(EditingModeText));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(CropSizeText));
        OnPropertyChanged(nameof(AudioSummaryText));
        OnPropertyChanged(nameof(CanSaveProject));
        OnPropertyChanged(nameof(CanOpenProject));
        OnPropertyChanged(nameof(CanNewProject));
        OnPropertyChanged(nameof(CanRemoveSelectedMedia));
        OnPropertyChanged(nameof(CanApplyCropPreset));
        OnPropertyChanged(nameof(CanApplyCropPresetToAll));
        OnPropertyChanged(nameof(CanMoveSelectedVideoLeft));
        OnPropertyChanged(nameof(CanMoveSelectedVideoRight));
        RaiseSequenceStateChanged();
        RaiseExportStateChanged();
    }

    private void ClearProjectContent()
    {
        SelectedMedia = null;
        SelectedVideoClip = null;
        foreach (var clip in VideoClips)
        {
            DetachVideoClip(clip);
            clip.Dispose();
        }

        VideoClips.Clear();
        foreach (var audioTrack in AudioTracks)
        {
            audioTrack.PropertyChanged -= OnAudioTrackPropertyChanged;
            CancelWaveformAnalysis(audioTrack);
            audioTrack.Dispose();
        }

        foreach (var mediaItem in MediaItems)
        {
            mediaItem.Dispose();
        }

        AudioTracks.Clear();
        MediaItems.Clear();
        _knownPaths.Clear();
        _pendingMediaIds.Clear();
        _unavailableProjectMedia.Clear();
        _timelineFrameCache.Clear();
        _isAudioMixerExpanded = false;
        _sequencePlayhead = MediaTime.Zero;
        _sequenceSelectionStart = MediaTime.Zero;
        _sequenceSelectionEnd = MediaTime.Zero;
        _sequenceTimelineZoom = 1;
        _sequenceTimelineViewportStart = 0;
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
            MarkProjectDirty();
        }

        if (eventArgs.PropertyName is nameof(MediaItemViewModel.SelectionStart) or
            nameof(MediaItemViewModel.SelectionEnd))
        {
            RaiseExportStateChanged();
        }

    }

    private void OnAudioTrackPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AudioTrackViewModel.Edit) or
            nameof(AudioTrackViewModel.GainDb) or
            nameof(AudioTrackViewModel.IsMuted) or
            nameof(AudioTrackViewModel.TimelineOffset))
        {
            MarkProjectDirty();
            RaiseExportStateChanged();
            OnPropertyChanged(nameof(PreviewAudioTracks));
        }

        if (eventArgs.PropertyName is nameof(AudioTrackViewModel.TimelineZoom) or
            nameof(AudioTrackViewModel.TimelineViewportStart))
        {
            StartWaveformAnalysis((AudioTrackViewModel)sender!, debounce: true);
        }
    }

    private void AddAudioTracks(MediaItemViewModel mediaItem)
    {
        if (mediaItem.Media is null)
        {
            return;
        }

        foreach (var stream in mediaItem.Media.Probe.AudioStreams)
        {
            if (AudioTracks.Any(track =>
                    PathComparer.Equals(track.SourcePath, mediaItem.SourcePath) &&
                    track.StreamIndex == stream.Index))
            {
                continue;
            }

            try
            {
                var track = new AudioTrackViewModel(mediaItem.Media, stream);
                track.PropertyChanged += OnAudioTrackPropertyChanged;
                AudioTracks.Add(track);
            }
            catch (ArgumentException)
            {
                // Streams without usable duration remain in probe details but cannot be edited yet.
            }
        }

        RaiseWorkspaceStateChanged();
        if (ShowAudioMixer)
        {
            foreach (var track in AudioTracks.Where(track => !track.HasWaveform))
            {
                StartWaveformAnalysis(track, debounce: false);
            }
        }
    }

    private void AddInitialVideoClip(MediaItemViewModel mediaItem)
    {
        var duration = mediaItem.Edit?.SourceDuration ?? mediaItem.Media?.Probe.Duration;
        if (duration is null || duration <= MediaTime.Zero)
        {
            return;
        }

        var previousDuration = SequenceDurationSeconds;
        var selectionCoveredWholeSequence =
            _sequenceSelectionStart == MediaTime.Zero &&
            Math.Abs(_sequenceSelectionEnd.TotalSeconds - previousDuration) < 0.001;
        var fullRange = new MediaRange(MediaTime.Zero, duration.Value);
        var model = new SequenceClip(Guid.NewGuid(), mediaItem.Id, fullRange, fullRange);
        var clip = new VideoClipViewModel(mediaItem, model, mediaItem.Crop);
        AttachVideoClip(clip);
        VideoClips.Add(clip);
        UpdateSequenceLayout(resetSelectionIfEmpty: false);

        if (selectionCoveredWholeSequence || VideoClips.Count == 1)
        {
            _sequenceSelectionStart = MediaTime.Zero;
            _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
            RaiseSequenceSelectionChanged();
        }

        SelectedVideoClip ??= clip;
        StartSequenceTimelineAnalysis(debounce: false);
    }

    private void AttachVideoClip(VideoClipViewModel clip)
    {
        clip.PropertyChanged += OnVideoClipPropertyChanged;
        clip.SourceWindowResized += OnVideoClipSourceWindowResized;
    }

    private void DetachVideoClip(VideoClipViewModel clip)
    {
        clip.PropertyChanged -= OnVideoClipPropertyChanged;
        clip.SourceWindowResized -= OnVideoClipSourceWindowResized;
    }

    private void OnVideoClipSourceWindowResized(object? sender, EventArgs eventArgs)
    {
        _ = eventArgs;
        if (_isApplyingCropPreset || sender is not VideoClipViewModel resizedClip)
        {
            return;
        }

        _isApplyingCropPreset = true;
        try
        {
            var resized = resizedClip.SourceWindow;
            var maximum = CropRegion.FullFrame(resized.SourceSize)
                .ResizeToAspectRatio(resized.Width, resized.Height);
            var scale = Math.Min(
                resized.Width / (double)maximum.Width,
                resized.Height / (double)maximum.Height);
            foreach (var clip in VideoClips.Where(clip => !ReferenceEquals(clip, resizedClip)))
            {
                var otherMaximum = CropRegion.FullFrame(clip.VideoSize)
                    .ResizeToAspectRatio(resized.Width, resized.Height);
                var width = Math.Clamp(
                    checked((int)Math.Round(otherMaximum.Width * scale)),
                    1,
                    clip.VideoSize.Width);
                var height = Math.Clamp(
                    checked((int)Math.Round(otherMaximum.Height * scale)),
                    1,
                    clip.VideoSize.Height);
                var centerX = clip.SourceWindow.X + (clip.SourceWindow.Width / 2d);
                var centerY = clip.SourceWindow.Y + (clip.SourceWindow.Height / 2d);
                var x = Math.Clamp(
                    checked((int)Math.Round(centerX - (width / 2d))),
                    0,
                    clip.VideoSize.Width - width);
                var y = Math.Clamp(
                    checked((int)Math.Round(centerY - (height / 2d))),
                    0,
                    clip.VideoSize.Height - height);
                clip.SourceWindow = new CropRegion(clip.VideoSize, x, y, width, height);
            }
        }
        finally
        {
            _isApplyingCropPreset = false;
        }

        if (!SelectedCropAspectPreset.IsCustom)
        {
            _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
            OnPropertyChanged(nameof(SelectedCropAspectPreset));
        }

        StatusText = "Crop resized manually; preset changed to Custom";
    }

    private void OnVideoClipPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender is not VideoClipViewModel clip)
        {
            return;
        }

        if (eventArgs.PropertyName == nameof(VideoClipViewModel.Model))
        {
            UpdateSequenceLayout(resetSelectionIfEmpty: false);
            StartSequenceTimelineAnalysis(debounce: true);
            MarkProjectDirty();
        }

        if (eventArgs.PropertyName == nameof(VideoClipViewModel.SourceWindow))
        {
            MarkProjectDirty();
            RaiseExportStateChanged();
        }
    }

    private void ReplaceVideoClips(
        IReadOnlyList<VideoClipViewModel> replacements,
        Guid? preferredClipId)
    {
        var retained = replacements.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var existing in VideoClips)
        {
            DetachVideoClip(existing);
            if (!retained.Contains(existing))
            {
                existing.Dispose();
            }
        }

        VideoClips.Clear();
        foreach (var replacement in replacements)
        {
            AttachVideoClip(replacement);
            VideoClips.Add(replacement);
        }

        UpdateSequenceLayout(resetSelectionIfEmpty: true);
        SelectedVideoClip = preferredClipId is { } id
            ? VideoClips.FirstOrDefault(clip => clip.Id == id)
            : VideoClips.FirstOrDefault();
        RaiseWorkspaceStateChanged();
    }

    private void UpdateSequenceLayout(bool resetSelectionIfEmpty)
    {
        var cursor = MediaTime.Zero;
        foreach (var clip in VideoClips)
        {
            clip.TimelineStart = cursor;
            cursor += clip.Duration;
        }

        var duration = cursor;
        if (_sequencePlayhead > duration)
        {
            _sequencePlayhead = duration;
        }

        if (_sequenceSelectionStart > duration)
        {
            _sequenceSelectionStart = duration;
        }

        if (_sequenceSelectionEnd > duration)
        {
            _sequenceSelectionEnd = duration;
        }

        if (resetSelectionIfEmpty && VideoClips.Count == 0)
        {
            _sequencePlayhead = MediaTime.Zero;
            _sequenceSelectionStart = MediaTime.Zero;
            _sequenceSelectionEnd = MediaTime.Zero;
        }

        _sequenceTimelineViewportStart = TimelineViewportMath.ClampStart(
            duration.TotalSeconds,
            SequenceTimelineZoom,
            _sequenceTimelineViewportStart);
        RaiseSequenceStateChanged();
    }

    private void CollapseSequenceSelection(MediaTime timelineTime)
    {
        var bounded = Min(timelineTime, SequenceTimeFromSeconds(SequenceDurationSeconds));
        _sequencePlayhead = bounded;
        _sequenceSelectionStart = bounded;
        _sequenceSelectionEnd = bounded;
        RaiseSequenceStateChanged();
        SyncSourcePreviewToSequenceTime(bounded, selectClip: true);
    }

    private VideoClipViewModel? FindClipAtTimelineTime(MediaTime timelineTime)
    {
        if (VideoClips.Count == 0)
        {
            return null;
        }

        return VideoClips.FirstOrDefault(clip =>
                   timelineTime >= clip.TimelineStart && timelineTime < clip.TimelineEnd) ??
               (timelineTime == VideoClips[^1].TimelineEnd ? VideoClips[^1] : null);
    }

    private void SyncSourcePreviewToSequenceTime(MediaTime timelineTime, bool selectClip)
    {
        var clip = FindClipAtTimelineTime(timelineTime);
        if (clip is null)
        {
            return;
        }

        if (selectClip)
        {
            SelectedVideoClip = clip;
        }

        var offset = Min(clip.Duration, Max(MediaTime.Zero, timelineTime - clip.TimelineStart));
        clip.Source.Playhead = Min(clip.SourceEnd, clip.SourceStart + offset);
    }

    private MediaRange NormalizedSequenceSelection() =>
        new(
            Min(_sequenceSelectionStart, _sequenceSelectionEnd),
            Max(_sequenceSelectionStart, _sequenceSelectionEnd));

    private IReadOnlyList<SequenceExportSlice> GetSequenceExportSlices()
    {
        if (VideoClips.Count == 0)
        {
            return [];
        }

        var selection = HasSequenceSelection
            ? NormalizedSequenceSelection()
            : new MediaRange(MediaTime.Zero, SequenceTimeFromSeconds(SequenceDurationSeconds));
        var slices = new List<SequenceExportSlice>(VideoClips.Count);
        foreach (var clip in VideoClips)
        {
            var timelineStart = Max(selection.Start, clip.TimelineStart);
            var timelineEnd = Min(selection.End, clip.TimelineEnd);
            if (timelineEnd <= timelineStart)
            {
                continue;
            }

            slices.Add(new SequenceExportSlice(
                clip,
                new MediaRange(
                    clip.SourceStart + (timelineStart - clip.TimelineStart),
                    clip.SourceStart + (timelineEnd - clip.TimelineStart))));
        }

        return slices;
    }

    private MediaTime SequenceTimeFromSeconds(double seconds)
    {
        var bounded = Math.Clamp(
            double.IsFinite(seconds) ? seconds : 0,
            0,
            Math.Max(0, SequenceDurationSeconds));
        return new MediaTime(checked((long)Math.Round(bounded * 1_000_000)), 1_000_000);
    }

    private void RaiseSequenceSelectionChanged()
    {
        OnPropertyChanged(nameof(SequenceSelectionRangeText));
        OnPropertyChanged(nameof(SequenceSelectedDurationText));
        OnPropertyChanged(nameof(HasSequenceSelection));
        OnPropertyChanged(nameof(CanRemoveSequenceSelection));
        OnPropertyChanged(nameof(CanKeepSequenceSelection));
        RaiseExportStateChanged();
    }

    private void RaiseSequenceViewportChanged()
    {
        OnPropertyChanged(nameof(SequenceTimelineZoom));
        OnPropertyChanged(nameof(SequenceTimelineViewportStart));
        OnPropertyChanged(nameof(SequenceTimelineViewportDuration));
        OnPropertyChanged(nameof(SequenceTimelineViewportEnd));
        OnPropertyChanged(nameof(SequenceTimelineZoomText));
        OnPropertyChanged(nameof(SequenceTimelineViewportText));
        OnPropertyChanged(nameof(CanZoomSequenceTimelineIn));
        OnPropertyChanged(nameof(CanZoomSequenceTimelineOut));
    }

    private void RaiseSequenceStateChanged()
    {
        OnPropertyChanged(nameof(SequenceDurationSeconds));
        OnPropertyChanged(nameof(SequencePlayheadSeconds));
        OnPropertyChanged(nameof(SequencePlayheadText));
        OnPropertyChanged(nameof(SequenceSelectionStartSeconds));
        OnPropertyChanged(nameof(SequenceSelectionEndSeconds));
        OnPropertyChanged(nameof(SequenceOutputDurationText));
        OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
        OnPropertyChanged(nameof(CanDeleteSelectedVideoClip));
        RaiseSequenceSelectionChanged();
        RaiseSequenceViewportChanged();
    }

    private void StartSequenceTimelineAnalysis(bool debounce)
    {
        StartCachedSequenceTimelineAnalysis(debounce);
    }

    private async Task RefreshSequenceTimelineAnalysisAsync(
        IReadOnlyList<VideoClipViewModel> clips,
        CancellationTokenSource request,
        bool debounce)
    {
        const int viewportThumbnailCount = 14;
        var token = request.Token;
        var generated = new Dictionary<VideoClipViewModel, List<TimelineThumbnailFrame>>();
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(180), token);
            }

            var viewportStart = SequenceTimelineViewportStart;
            var viewportEnd = SequenceTimelineViewportEnd;
            var viewportDuration = Math.Max(0.000001, viewportEnd - viewportStart);
            foreach (var clip in clips)
            {
                var visibleStart = Math.Max(viewportStart, clip.TimelineStartSeconds);
                var visibleEnd = Math.Min(viewportEnd, clip.TimelineEndSeconds);
                if (visibleEnd <= visibleStart ||
                    clip.Source.Media?.Probe.VideoStreams.FirstOrDefault() is not { } video)
                {
                    continue;
                }

                clip.IsTimelineLoading = true;
                var visibleDuration = visibleEnd - visibleStart;
                var count = Math.Clamp(
                    (int)Math.Ceiling(viewportThumbnailCount * visibleDuration / viewportDuration),
                    1,
                    viewportThumbnailCount);
                var sourceVisibleStart = clip.SourceStart.TotalSeconds + (visibleStart - clip.TimelineStartSeconds);
                var sourceVisibleEnd = clip.SourceStart.TotalSeconds + (visibleEnd - clip.TimelineStartSeconds);
                var cellDuration = (sourceVisibleEnd - sourceVisibleStart) / count;
                var frames = new List<TimelineThumbnailFrame>(count);
                generated.Add(clip, frames);

                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var cellStart = sourceVisibleStart + (index * cellDuration);
                    var cellEnd = index == count - 1
                        ? sourceVisibleEnd
                        : Math.Min(sourceVisibleEnd, cellStart + cellDuration);
                    if (cellEnd <= cellStart)
                    {
                        continue;
                    }

                    var timestamp = cellStart + ((cellEnd - cellStart) / 2);
                    await _analysisSlots.WaitAsync(token);
                    DecodedFrame decoded;
                    try
                    {
                        decoded = await _frameDecoder!.DecodeAsync(
                            clip.SourcePath,
                            video.Index,
                            ToMediaTime(timestamp),
                            new PixelSize(240, 120),
                            token);
                    }
                    finally
                    {
                        _analysisSlots.Release();
                    }

                    await using var stream = new MemoryStream(decoded.EncodedImage.ToArray(), writable: false);
                    token.ThrowIfCancellationRequested();
                    frames.Add(new TimelineThumbnailFrame(cellStart, cellEnd, timestamp, new Bitmap(stream)));
                }
            }

            if (!ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                return;
            }

            foreach (var clip in clips)
            {
                if (!VideoClips.Contains(clip))
                {
                    continue;
                }

                if (generated.Remove(clip, out var frames))
                {
                    clip.SetTimelineThumbnails(frames.ToArray());
                    frames.Clear();
                }
                else
                {
                    clip.SetTimelineThumbnails([]);
                }

                clip.IsTimelineLoading = false;
            }

            _sequenceTimelineVisualRevision++;
            OnPropertyChanged(nameof(SequenceTimelineVisualRevision));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer sequence or viewport superseded this filmstrip request.
        }
        catch (Exception exception) when (exception is FrameDecodeException or IOException)
        {
            if (ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                StatusText = $"Timeline thumbnails are unavailable: {exception.Message}";
            }
        }
        finally
        {
            foreach (var frames in generated.Values)
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }

            foreach (var clip in clips)
            {
                if (VideoClips.Contains(clip))
                {
                    clip.IsTimelineLoading = false;
                }
            }

            if (ReferenceEquals(_sequenceTimelineAnalysisCancellation, request))
            {
                _sequenceTimelineAnalysisCancellation = null;
            }

            request.Dispose();
        }
    }

    private void StartTimelineHoverPreview(double timelineSeconds)
    {
        StartCachedTimelineHoverPreview(timelineSeconds);
    }

    private async Task RefreshTimelineHoverPreviewAsync(
        VideoClipViewModel clip,
        int videoStreamIndex,
        MediaTime sourceTime,
        CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(65), token);
            var decoded = await _frameDecoder!.DecodeAsync(
                clip.SourcePath,
                videoStreamIndex,
                Min(sourceTime, clip.SourceEnd),
                new PixelSize(360, 202),
                token);
            await using var stream = new MemoryStream(decoded.EncodedImage.ToArray(), writable: false);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_timelineHoverCancellation, request))
            {
                TimelineHoverPreviewImage = new Bitmap(stream);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is FrameDecodeException or IOException)
        {
            if (ReferenceEquals(_timelineHoverCancellation, request))
            {
                TimelineHoverPreviewImage = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_timelineHoverCancellation, request))
            {
                _timelineHoverCancellation = null;
            }

            request.Dispose();
        }
    }

    private static string FormatSequenceTimestamp(MediaTime value)
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

    private static MediaTime Max(MediaTime left, MediaTime right) => left >= right ? left : right;

    private void RaiseExportStateChanged()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportAvailabilityText));
        OnPropertyChanged(nameof(HasExportBlockingIssue));
        OnPropertyChanged(nameof(CanFixExportCompatibility));
        OnPropertyChanged(nameof(ExportCompatibilityActionText));
        OnPropertyChanged(nameof(ExportPlanSummary));
    }

    private bool TryCreateMediaDocument(
        MediaItemViewModel item,
        out ProjectMediaDocument? document)
    {
        document = null;
        if (item.Media is null)
        {
            return false;
        }

        var duration = item.Edit?.SourceDuration ?? item.Media.Probe.Duration;
        if (duration is null || duration <= MediaTime.Zero)
        {
            return false;
        }

        var crop = item.HasVideo
            ? item.Crop
            : CropRegion.FullFrame(new PixelSize(1, 1));
        var ranges = item.Edit?.KeptRanges ??
                     [new MediaRange(MediaTime.Zero, duration.Value)];
        var audioTracks = AudioTracks
            .Where(track => PathComparer.Equals(track.SourcePath, item.SourcePath))
            .Select(track => new ProjectAudioTrackDocument(
                track.StreamIndex,
                track.GainDb,
                track.IsMuted,
                track.Edit.SourceDuration.Numerator,
                track.Edit.SourceDuration.Denominator,
                track.KeptRanges.Select(CreateRangeDocument).ToArray(),
                track.TimelineOffset.Numerator,
                track.TimelineOffset.Denominator))
            .ToArray();
        document = new ProjectMediaDocument(
            item.SourcePath,
            item.Media.Probe.FileSizeBytes,
            crop.SourceSize.Width,
            crop.SourceSize.Height,
            crop.X,
            crop.Y,
            crop.Width,
            crop.Height,
            duration.Value.Numerator,
            duration.Value.Denominator,
            ranges.Select(CreateRangeDocument).ToArray(),
            audioTracks,
            item.Id);
        return true;
    }

    private static ProjectVideoClipDocument CreateVideoClipDocument(VideoClipViewModel clip)
    {
        var source = clip.Model.SourceRange;
        var available = clip.Model.AvailableRange;
        var window = clip.SourceWindow;
        return new ProjectVideoClipDocument(
            clip.Id,
            clip.Source.Id,
            source.Start.Numerator,
            source.Start.Denominator,
            source.End.Numerator,
            source.End.Denominator,
            available.Start.Numerator,
            available.Start.Denominator,
            available.End.Numerator,
            available.End.Denominator,
            window.X,
            window.Y,
            window.Width,
            window.Height);
    }

    private void RestoreVideoSequence(ProjectDocument document, ICollection<string> warnings)
    {
        var replacements = new List<VideoClipViewModel>();
        if (document.SchemaVersion >= 2 && document.VideoClips is not null)
        {
            var mediaById = MediaItems.ToDictionary(item => item.Id);
            foreach (var savedClip in document.VideoClips)
            {
                if (!mediaById.TryGetValue(savedClip.SourceMediaId, out var source) || !source.HasVideo)
                {
                    warnings.Add("A timeline clip refers to unavailable media");
                    continue;
                }

                try
                {
                    var model = new SequenceClip(
                        savedClip.ClipId,
                        savedClip.SourceMediaId,
                        new MediaRange(
                            new MediaTime(savedClip.SourceStartNumerator, savedClip.SourceStartDenominator),
                            new MediaTime(savedClip.SourceEndNumerator, savedClip.SourceEndDenominator)),
                        new MediaRange(
                            new MediaTime(savedClip.AvailableStartNumerator, savedClip.AvailableStartDenominator),
                            new MediaTime(savedClip.AvailableEndNumerator, savedClip.AvailableEndDenominator)));
                    var window = new CropRegion(
                        source.VideoSize,
                        savedClip.SourceWindowX,
                        savedClip.SourceWindowY,
                        savedClip.SourceWindowWidth,
                        savedClip.SourceWindowHeight);
                    replacements.Add(new VideoClipViewModel(source, model, window));
                }
                catch (ArgumentException exception)
                {
                    warnings.Add($"A timeline clip could not be restored: {exception.Message}");
                }
            }

            _selectedCropAspectPreset = CropAspectPresets.FirstOrDefault(preset =>
                                            preset.Id == document.CropSettings?.PresetId) ??
                                        BuiltInCropAspectPresets.Custom;
            _isCropAspectLocked = document.CropSettings?.IsAspectLocked == true;
        }
        else
        {
            foreach (var source in MediaItems.Where(item => item.HasVideo))
            {
                var duration = source.Edit?.SourceDuration ?? source.Media?.Probe.Duration;
                if (duration is null || duration <= MediaTime.Zero)
                {
                    continue;
                }

                var available = new MediaRange(MediaTime.Zero, duration.Value);
                foreach (var range in source.Edit?.KeptRanges ?? [available])
                {
                    replacements.Add(new VideoClipViewModel(
                        source,
                        new SequenceClip(Guid.NewGuid(), source.Id, range, available),
                        source.Crop));
                }
            }

            _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
            _isCropAspectLocked = false;
        }

        ReplaceVideoClips(replacements, replacements.FirstOrDefault()?.Id);
        _sequencePlayhead = MediaTime.Zero;
        _sequenceSelectionStart = MediaTime.Zero;
        _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
        _sequenceTimelineZoom = 1;
        _sequenceTimelineViewportStart = 0;
        OnPropertyChanged(nameof(SelectedCropAspectPreset));
        OnPropertyChanged(nameof(IsCropAspectLocked));
        RaiseSequenceStateChanged();
        SyncSourcePreviewToSequenceTime(MediaTime.Zero, selectClip: true);
        StartSequenceTimelineAnalysis(debounce: false);
    }

    private bool TryRestoreMedia(
        MediaItemViewModel item,
        ProjectMediaDocument document,
        out string? warning)
    {
        warning = null;
        var expectedSize = new PixelSize(document.SourceWidth, document.SourceHeight);
        if ((item.HasVideo && expectedSize != item.VideoSize) ||
            (document.ExpectedFileSizeBytes is { } expectedBytes &&
             item.Media?.Probe.FileSizeBytes is { } actualBytes &&
             expectedBytes != actualBytes))
        {
            warning = $"{item.DisplayName} changed since the project was saved; its edits were preserved but not applied";
            return false;
        }

        try
        {
            if (item.HasVideo)
            {
                var duration = new MediaTime(
                    document.SourceDurationNumerator,
                    document.SourceDurationDenominator);
                var edit = SourceEdit.FromKeptRanges(
                    duration,
                    document.KeptRanges.Select(CreateMediaRange));
                var crop = new CropRegion(
                    expectedSize,
                    document.CropX,
                    document.CropY,
                    document.CropWidth,
                    document.CropHeight);
                item.RestoreEditing(crop, edit);
            }

            foreach (var savedAudio in document.AudioTracks ?? [])
            {
                var track = AudioTracks.FirstOrDefault(candidate =>
                    PathComparer.Equals(candidate.SourcePath, item.SourcePath) &&
                    candidate.StreamIndex == savedAudio.StreamIndex) ??
                    throw new ArgumentException($"Audio stream {savedAudio.StreamIndex} is no longer available.");
                var audioEdit = SourceEdit.FromKeptRanges(
                    new MediaTime(
                        savedAudio.SourceDurationNumerator,
                        savedAudio.SourceDurationDenominator),
                    savedAudio.KeptRanges.Select(CreateMediaRange));
                track.Restore(
                    audioEdit,
                    savedAudio.GainDb,
                    savedAudio.IsMuted,
                    new MediaTime(
                        savedAudio.TimelineOffsetNumerator,
                        savedAudio.TimelineOffsetDenominator));
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            warning = $"{item.DisplayName} could not restore its saved edits: {exception.Message}";
            return false;
        }
    }

    private static ProjectRangeDocument CreateRangeDocument(MediaRange range)
    {
        return new ProjectRangeDocument(
            range.Start.Numerator,
            range.Start.Denominator,
            range.End.Numerator,
            range.End.Denominator);
    }

    private static MediaRange CreateMediaRange(ProjectRangeDocument range)
    {
        return new MediaRange(
            new MediaTime(range.StartNumerator, range.StartDenominator),
            new MediaTime(range.EndNumerator, range.EndDenominator));
    }

    private void MarkProjectDirty()
    {
        if (_isLoadingProject)
        {
            return;
        }

        IsProjectDirty = true;
        ScheduleAutosave();
    }

    private void ScheduleAutosave()
    {
        if (_projectStore is null || string.IsNullOrWhiteSpace(_recoveryDirectory))
        {
            return;
        }

        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        var request = new CancellationTokenSource();
        _autosaveCancellation = request;
        _ = AutosaveAfterDelayAsync(request);
    }

    private async Task AutosaveAfterDelayAsync(CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(_autosaveDelay, request.Token);
            if (_projectStore is null || !IsProjectDirty)
            {
                return;
            }

            await _projectStore.SaveAsync(
                GetRecoveryPath(),
                CreateProjectDocument(),
                request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer edit superseded this autosave request.
        }
        catch (ProjectStoreException exception)
        {
            StatusText = $"Autosave warning: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_autosaveCancellation, request))
            {
                request.Dispose();
                _autosaveCancellation = null;
            }
        }
    }

    private async Task DeleteRecoveryAsync(CancellationToken cancellationToken)
    {
        if (_projectStore is null || string.IsNullOrWhiteSpace(_recoveryDirectory))
        {
            return;
        }

        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = null;
        try
        {
            await _projectStore.DeleteIfExistsAsync(GetRecoveryPath(), cancellationToken);
        }
        catch (ProjectStoreException)
        {
            // The durable project was saved successfully; stale recovery cleanup is non-fatal.
        }
    }

    private string GetRecoveryPath()
    {
        return Path.Combine(_recoveryDirectory!, $"{_projectId:N}.recovery.clipedit");
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
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
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

    private void StartTimelineAnalysis(MediaItemViewModel? mediaItem, bool debounce)
    {
        _timelineAnalysisCancellation?.Cancel();
        _timelineAnalysisCancellation = null;
        if (mediaItem?.Media?.Probe.VideoStreams.FirstOrDefault() is null || _frameDecoder is null)
        {
            return;
        }

        var request = new CancellationTokenSource();
        _timelineAnalysisCancellation = request;
        _ = RefreshTimelineAnalysisAsync(mediaItem, request, debounce);
    }

    private async Task RefreshTimelineAnalysisAsync(
        MediaItemViewModel mediaItem,
        CancellationTokenSource request,
        bool debounce)
    {
        const int thumbnailCount = 12;
        var cancellationToken = request.Token;
        var thumbnails = new List<TimelineThumbnailFrame>(thumbnailCount);
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken);
            }

            var video = mediaItem.Media!.Probe.VideoStreams.First();
            var start = mediaItem.TimelineViewportStart;
            var duration = mediaItem.TimelineViewportDurationSeconds;
            if (duration <= 0)
            {
                return;
            }

            mediaItem.IsTimelineLoading = true;
            mediaItem.TimelineErrorText = null;
            var cellDuration = duration / thumbnailCount;
            for (var index = 0; index < thumbnailCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cellStart = start + (index * cellDuration);
                var cellEnd = Math.Min(mediaItem.SourceDurationSeconds, cellStart + cellDuration);
                if (cellEnd <= cellStart)
                {
                    break;
                }

                var timestamp = cellStart + ((cellEnd - cellStart) / 2);
                await _analysisSlots.WaitAsync(cancellationToken);
                DecodedFrame decodedFrame;
                try
                {
                    decodedFrame = await _frameDecoder!.DecodeAsync(
                        mediaItem.SourcePath,
                        video.Index,
                        ToMediaTime(timestamp),
                        new PixelSize(240, 112),
                        cancellationToken);
                }
                finally
                {
                    _analysisSlots.Release();
                }
                await using var stream = new MemoryStream(decodedFrame.EncodedImage.ToArray(), writable: false);
                cancellationToken.ThrowIfCancellationRequested();
                thumbnails.Add(new TimelineThumbnailFrame(cellStart, cellEnd, timestamp, new Bitmap(stream)));
            }

            if (ReferenceEquals(_timelineAnalysisCancellation, request) &&
                ReferenceEquals(SelectedMedia, mediaItem))
            {
                mediaItem.SetTimelineThumbnails(thumbnails.ToArray());
                thumbnails.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A new source or viewport superseded this thumbnail request.
        }
        catch (FrameDecodeException exception)
        {
            if (ReferenceEquals(_timelineAnalysisCancellation, request))
            {
                mediaItem.TimelineErrorText = exception.Message;
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_timelineAnalysisCancellation, request))
            {
                mediaItem.TimelineErrorText = $"Could not generate timeline previews: {exception.Message}";
            }
        }
        finally
        {
            foreach (var thumbnail in thumbnails)
            {
                thumbnail.Dispose();
            }

            if (ReferenceEquals(_timelineAnalysisCancellation, request))
            {
                mediaItem.IsTimelineLoading = false;
                _timelineAnalysisCancellation = null;
            }

            request.Dispose();
        }
    }

    private void StartWaveformAnalysis(AudioTrackViewModel track, bool debounce)
    {
        if (_waveformRenderer is null)
        {
            return;
        }

        CancelWaveformAnalysis(track);
        var request = new CancellationTokenSource();
        _waveformCancellations.Add(track, request);
        _ = RefreshWaveformAsync(track, request, debounce);
    }

    private async Task RefreshWaveformAsync(
        AudioTrackViewModel track,
        CancellationTokenSource request,
        bool debounce)
    {
        var cancellationToken = request.Token;
        TimelineBitmapVisual? visual = null;
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken);
            }

            var start = track.TimelineViewportStart;
            var end = track.TimelineViewportEndSeconds;
            if (end <= start)
            {
                return;
            }

            track.IsWaveformLoading = true;
            track.WaveformErrorText = null;
            await _analysisSlots.WaitAsync(cancellationToken);
            WaveformImage image;
            try
            {
                image = await _waveformRenderer!.RenderAsync(
                    track.SourcePath,
                    track.StreamIndex,
                    new MediaRange(ToMediaTime(start), ToMediaTime(end)),
                    new PixelSize(1_600, 72),
                    cancellationToken);
            }
            finally
            {
                _analysisSlots.Release();
            }
            await using var stream = new MemoryStream(image.EncodedImage.ToArray(), writable: false);
            cancellationToken.ThrowIfCancellationRequested();
            visual = new TimelineBitmapVisual(start, end, new Bitmap(stream));
            if (_waveformCancellations.TryGetValue(track, out var activeRequest) &&
                ReferenceEquals(activeRequest, request))
            {
                track.SetWaveform(visual);
                visual = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer waveform viewport superseded this request.
        }
        catch (WaveformRenderException exception)
        {
            if (_waveformCancellations.TryGetValue(track, out var activeRequest) &&
                ReferenceEquals(activeRequest, request))
            {
                track.WaveformErrorText = exception.Message;
            }
        }
        catch (Exception exception)
        {
            if (_waveformCancellations.TryGetValue(track, out var activeRequest) &&
                ReferenceEquals(activeRequest, request))
            {
                track.WaveformErrorText = $"Could not generate waveform: {exception.Message}";
            }
        }
        finally
        {
            visual?.Dispose();
            if (_waveformCancellations.TryGetValue(track, out var activeRequest) &&
                ReferenceEquals(activeRequest, request))
            {
                track.IsWaveformLoading = false;
                _waveformCancellations.Remove(track);
            }

            request.Dispose();
        }
    }

    private void CancelWaveformAnalysis(AudioTrackViewModel track)
    {
        if (_waveformCancellations.Remove(track, out var request))
        {
            request.Cancel();
        }
    }

    private static MediaTime ToMediaTime(double seconds)
    {
        var microseconds = checked((long)Math.Round(Math.Max(0, seconds) * 1_000_000));
        return new MediaTime(microseconds, 1_000_000);
    }

    private int GetSelectedVideoIndex()
    {
        return GetSelectedVideoIndex(VideoItems.ToArray());
    }

    private int GetSelectedVideoIndex(IReadOnlyList<MediaItemViewModel> videos)
    {
        return SelectedMedia is null ? -1 : videos.IndexOf(SelectedMedia);
    }
}

file static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> items, T value)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(items[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}

internal readonly record struct SequenceExportSlice(
    VideoClipViewModel Clip,
    MediaRange SourceRange);
