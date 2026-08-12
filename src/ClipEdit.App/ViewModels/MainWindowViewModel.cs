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

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly HashSet<string> _knownPaths = new(PathComparer);
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
    private bool _isBusy;
    private bool _isPreviewLoading;
    private Bitmap? _previewImage;
    private string? _previewErrorText;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _timelineAnalysisCancellation;
    private readonly Dictionary<AudioTrackViewModel, CancellationTokenSource> _waveformCancellations = [];
    private readonly SemaphoreSlim _analysisSlots = new(initialCount: 2, maxCount: 2);
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
                StartTimelineAnalysis(value, debounce: false);
                OnPropertyChanged(nameof(CanRemoveSelectedMedia));
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

            if (SelectedMedia?.Media is null || !SelectedMedia.HasVideo)
            {
                return "Select an imported video to export";
            }

            if (VideoItems.Count() > 1)
            {
                return "Multi-video export will be enabled with the sequence timeline";
            }

            if (SelectedMedia.GetExportEdit() is not { } exportEdit || exportEdit.IsEmpty)
            {
                return "The selected range contains no kept video";
            }

            if (SelectedExportPreset.RequiresEvenDimensions &&
                (((SelectedMedia.Crop.Width & 1) != 0) || ((SelectedMedia.Crop.Height & 1) != 0)))
            {
                return $"{SelectedExportPreset.DisplayName} needs an even crop width and height";
            }

            return "Ready to export";
        }
    }

    public string ExportPlanSummary => SelectedMedia?.GetExportEdit() is null
        ? SelectedExportPreset.DisplayName
        : $"{SelectedExportPreset.DisplayName} · exact re-encode · " +
          $"{SelectedMedia.CropSizeText} · {SelectedMedia.SelectedExportDurationText}";

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

    public bool ShowTimeline => MediaItems.Count(item => item.HasVideo) > 1;

    public bool HasAudioTracks => AudioTracks.Count > 0;

    public bool ShowAudioMixer =>
        HasAudioTracks && (_isAudioMixerExpanded || ExternalAudioItems.Any());

    public bool ShowRangeStrip => ShowQuickWorkspace && !ShowTimeline;

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
            AddAudioTracks(item);
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
        if (!CanExport ||
            _exportRenderer is null ||
            SelectedMedia?.Media is null ||
            SelectedMedia.GetExportEdit() is not { } exportEdit)
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
                exportEdit,
                SelectedMedia.Crop,
                SelectedExportPreset,
                destinationPath,
                replaceExistingDestination,
                AudioTracks
                    .Where(track =>
                        !track.IsMuted &&
                        !track.Edit.IsEmpty &&
                        (track.IsExternal ||
                         PathComparer.Equals(track.SourcePath, SelectedMedia.SourcePath)))
                    .Select(track => track.IsExternal
                        ? new ExportAudioTrackPlan(
                            track.SourcePath,
                            track.StreamIndex,
                            track.GainDb,
                            track.TimelineOffset,
                            track.Edit)
                        : new ExportAudioTrackPlan(
                            track.StreamIndex,
                            track.GainDb,
                            track.Edit))
                    .ToImmutableArray());
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

            await ImportFilesAsync(document.Media.Select(media => media.SourcePath), cancellationToken);
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
            mediaDocuments);
    }

    public void Dispose()
    {
        if (SelectedMedia is not null)
        {
            SelectedMedia.PropertyChanged -= OnSelectedMediaPropertyChanged;
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
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _exportCancellation = null;
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = null;
        PreviewImage = null;
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
        RaiseExportStateChanged();
    }

    private void ClearProjectContent()
    {
        SelectedMedia = null;
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
        _isAudioMixerExpanded = false;
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

        if (eventArgs.PropertyName is nameof(MediaItemViewModel.TimelineZoom) or
            nameof(MediaItemViewModel.TimelineViewportStart))
        {
            StartTimelineAnalysis((MediaItemViewModel?)sender, debounce: true);
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

    private void RaiseExportStateChanged()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportAvailabilityText));
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
            audioTracks);
        return true;
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
}
