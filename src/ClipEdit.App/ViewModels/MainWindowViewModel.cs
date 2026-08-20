using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using ClipEdit.App.Recovery;
using ClipEdit.App.Updates;
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
    private ImportMediaUseCase? _importMedia;
    private IFrameDecoder? _frameDecoder;
    private IWaveformRenderer? _waveformRenderer;
    private IExportRenderer? _exportRenderer;
    private readonly SingleSourceExportPlanner _exportPlanner = new();
    private readonly IProjectStore? _projectStore;
    private readonly string? _recoveryDirectory;
    private readonly TimeSpan _autosaveDelay;
    private readonly List<ProjectMediaDocument> _unavailableProjectMedia = [];
    private readonly Dictionary<string, string> _pendingProjectRelinks = new(PathComparer);
    private ProjectDocument? _pendingProjectDocument;
    private string? _pendingProjectPath;
    private bool _pendingProjectIsRecovery;
    private bool _pendingProjectDiscardUnsavedChanges;
    private MediaItemViewModel? _selectedMedia;
    private VideoClipViewModel? _selectedVideoClip;
    private bool _isBusy;
    private bool _isPreviewLoading;
    private Bitmap? _previewImage;
    private TimelineFrameCacheKey? _previewCacheKey;
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
    private int _exportScalePercent = ExportEncodingSettings.DefaultScalePercent;
    private int _exportQuality = ExportEncodingSettings.DefaultQuality;
    private int _gifFrameRate = ExportEncodingSettings.DefaultGifFrameRate;
    private int _exportPlaybackSpeedPercent = ExportEncodingSettings.DefaultPlaybackSpeedPercent;
    private ExportQualityChoice _selectedExportQuality = ExportQualityChoice.MatchSource;
    private ExportEncodingSpeedChoice _selectedExportEncodingSpeed = ExportEncodingSpeedChoice.Balanced;
    private ExportHardwareAccelerationChoice _selectedExportHardwareAcceleration = ExportHardwareAccelerationChoice.Software;
    private bool _rememberExportAdjustments;
    private ExportDestinationChoice _selectedExportDestination = ExportDestinationChoice.File;
    private bool _isExporting;
    private double _exportProgress;
    private string _exportPhaseText = string.Empty;
    private string _statusText = "Ready";
    private Guid _projectId = Guid.NewGuid();
    private string? _projectPath;
    private bool _isProjectDirty;
    private bool _isLoadingProject;
    private bool _isAdvancedMode;
    private bool _isTimelineSnappingEnabled = true;
    private bool _moveTimelineClipsByDefault;
    private VideoClipClipboard? _videoClipClipboard;
    private CropAspectPreset _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
    private bool _isCropAspectLocked;
    private bool _isApplyingCropPreset;
    private MediaTime _sequencePlayhead;
    private MediaTime _sequenceSelectionStart;
    private MediaTime _sequenceSelectionEnd;
    private double _sequenceTimelineZoom = 1;
    private double _sequenceTimelineViewportStart;
    private bool _isSequenceTimelineFreeMode;
    private bool _isSequencePlayheadInGap;
    private bool _isSynchronizingAudioTimeline;
    private bool _isClipTransformEditActive;
    private bool _clipTransformChangedDuringEdit;
    private bool _clipTransformEditCreatesDistinctHistoryEntry;

    public MainWindowViewModel(
        IMediaProbe? mediaProbe,
        IFrameDecoder? frameDecoder = null,
        IExportRenderer? exportRenderer = null,
        IProjectStore? projectStore = null,
        string? recoveryDirectory = null,
        TimeSpan? autosaveDelay = null,
        IWaveformRenderer? waveformRenderer = null,
        IKeyframeProbe? keyframeProbe = null)
    {
        _frameDecoder = frameDecoder;
        _waveformRenderer = waveformRenderer;
        _keyframeProbe = keyframeProbe ?? mediaProbe as IKeyframeProbe;
        _exportRenderer = exportRenderer;
        ConfigureExportHardwareCapabilityProbe(exportRenderer as IExportHardwareCapabilityProbe);
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
            StatusText = "ffprobe was not found. Configure it in Media runtime settings to import media.";
        }

        ResetEditHistory();
    }

    public string ProductName => "ClipEdit";

    public UpdateViewModel Updates { get; private set; } = new();

    internal void ConfigureUpdates(UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        Updates.Dispose();
        Updates = updates;
        OnPropertyChanged(nameof(Updates));
    }

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

    public ObservableCollection<RecoveryCandidateViewModel> RecoveryCandidates { get; } = [];

    public ObservableCollection<MissingMediaReferenceViewModel> MissingMediaReferences { get; } = [];

    public bool IsProjectPersistenceAvailable => _projectStore is not null;

    public bool HasRecoveryCandidates => RecoveryCandidates.Count > 0;

    public bool HasPendingMissingMedia => MissingMediaReferences.Count > 0;

    public string PendingProjectOpenTitle => _pendingProjectIsRecovery
        ? "Relink media before recovery"
        : "Relink media before opening";

    public string PendingProjectOpenDescription =>
        "ClipEdit has not replaced the current workspace. Use a nearby suggestion or choose the original file at its new location; every replacement must match the saved media fingerprint.";

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
        IsProjectPersistenceAvailable && !IsBusy && !IsExporting;

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

    public bool IsClipTransformEditing => _isClipTransformEditActive;

    internal void BeginClipTransformEdit(bool createDistinctHistoryEntry = false)
    {
        if (_isClipTransformEditActive)
        {
            return;
        }

        _isClipTransformEditActive = true;
        _clipTransformChangedDuringEdit = false;
        _clipTransformEditCreatesDistinctHistoryEntry = createDistinctHistoryEntry;
        OnPropertyChanged(nameof(IsClipTransformEditing));
    }

    internal void EndClipTransformEdit()
    {
        if (!_isClipTransformEditActive)
        {
            return;
        }

        _isClipTransformEditActive = false;
        var createDistinctHistoryEntry = _clipTransformEditCreatesDistinctHistoryEntry;
        _clipTransformEditCreatesDistinctHistoryEntry = false;
        OnPropertyChanged(nameof(IsClipTransformEditing));
        if (!_clipTransformChangedDuringEdit)
        {
            return;
        }

        _clipTransformChangedDuringEdit = false;
        var clip = SelectedVideoClip;
        MarkProjectDirty(
            createDistinctHistoryEntry
                ? null
                : clip is null
                    ? "clip:visual-transform"
                    : $"clip:{clip.Id}:visual-transform");
        RaiseExportStateChanged();
    }

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

    public ExportPreset GetEffectiveExportPreset()
    {
        var slices = GetSequenceExportSlices();
        return ResolveSelectedExportPreset(slices);
    }

    private MatchInputExportResolution? ResolveMatchInput(IReadOnlyList<SequenceExportSlice> slices)
    {
        if (SelectedExportPreset.ParameterMode != ExportParameterMode.MatchInput || slices.Count == 0)
        {
            return null;
        }

        return MatchInputExportPresetResolver.Resolve(slices[0].Clip.Source.Media!.Probe);
    }

    private ExportPreset ResolveSelectedExportPreset(IReadOnlyList<SequenceExportSlice> slices)
    {
        var preset = ResolveMatchInput(slices) is { } match
            ? match.Preset
            : IsCustomExport
                ? CreateCustomExportPreset()
                : SelectedExportPreset;
        if (ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.MatchSource &&
            slices.Count > 0 &&
            preset.VideoCodec != VideoCodecFamily.Gif)
        {
            preset = MatchInputExportPresetResolver.ApplySourceQuality(
                preset,
                slices[0].Clip.Source.Media!.Probe);
        }

        return preset;
    }

    private ExportStrategy ResolveExportStrategy(
        IReadOnlyList<SequenceExportSlice> slices,
        ExportPreset preset) => ResolveExportStrategyDecision(slices, preset).Strategy;

    private PacketCopyDecision ResolveExportStrategyDecision(
        IReadOnlyList<SequenceExportSlice> slices,
        ExportPreset preset)
    {
        var blockers = PacketCopyBlocker.None;
        var reasons = new List<string>();
        var forceEditListStreamCopy = false;
        var forceVideoStreamCopy = false;
        var forceBoundaryGop = false;

        void Block(PacketCopyBlocker blocker, string reason)
        {
            blockers |= blocker;
            reasons.Add(reason);
        }

        PacketCopyDecision Finish()
        {
            if (blockers == PacketCopyBlocker.None)
            {
                return forceBoundaryGop
                    ? PacketCopyDecision.BoundaryGop(blockers, reasons)
                    : forceEditListStreamCopy
                        ? PacketCopyDecision.CopyEditListTrim(reasons)
                    : forceVideoStreamCopy
                        ? PacketCopyDecision.CopyVideo(blockers, reasons)
                        : PacketCopyDecision.Copy;
            }

            const PacketCopyBlocker audioOnlyBlockers =
                PacketCopyBlocker.ExternalAudio |
                PacketCopyBlocker.AudioLayout |
                PacketCopyBlocker.AudioGain |
                PacketCopyBlocker.AudioEdit |
                PacketCopyBlocker.AudioCodec;
            if ((blockers & ~audioOnlyBlockers) != PacketCopyBlocker.None)
            {
                return PacketCopyDecision.Transcode(blockers, reasons);
            }

            if (forceEditListStreamCopy && !forceVideoStreamCopy)
            {
                return PacketCopyDecision.Transcode(blockers, reasons);
            }

            return forceBoundaryGop
                ? PacketCopyDecision.BoundaryGop(blockers, reasons)
                : PacketCopyDecision.CopyVideo(blockers, reasons);
        }

        if (ExportQualityMode != ClipEdit.Media.Export.ExportQualityMode.MatchSource)
        {
            Block(PacketCopyBlocker.Quality, "Quality is set to Custom instead of Match input.");
        }
        if (ExportScalePercent != ExportEncodingSettings.DefaultScalePercent)
        {
            Block(PacketCopyBlocker.ExportScale, "Export scale is not 100%.");
        }
        if (ExportPlaybackSpeedPercent != ExportEncodingSettings.DefaultPlaybackSpeedPercent)
        {
            Block(PacketCopyBlocker.ExportSpeed, "Export playback speed is not 100%.");
        }
        if (preset.VideoCodec == VideoCodecFamily.Gif)
        {
            Block(PacketCopyBlocker.Format, "GIF always requires encoding.");
        }
        if (slices.Count > 1)
        {
            IReadOnlyList<string> concatReasons = [];
            if (blockers == PacketCopyBlocker.None &&
                TryResolveConcatStreamCopy(slices, preset, out concatReasons))
            {
                return PacketCopyDecision.CopyConcat;
            }

            if (blockers == PacketCopyBlocker.None)
            {
                foreach (var reason in concatReasons)
                {
                    Block(PacketCopyBlocker.SequenceCompatibility, reason);
                }
            }
            return PacketCopyDecision.Transcode(blockers, reasons);
        }
        if (slices.Count != 1)
        {
            Block(PacketCopyBlocker.ClipCount, "Export exactly one complete clip for packet copy.");
            return PacketCopyDecision.Transcode(blockers, reasons);
        }

        var slice = slices[0];
        var clip = slice.Clip;
        var probe = clip.Source.Media?.Probe;
        var video = probe?.VideoStreams.FirstOrDefault();
        var sourceDuration = clip.Source.Edit?.SourceDuration ?? probe?.Duration;
        var exportRange = HasSequenceSelection
            ? NormalizedSequenceSelection()
            : new MediaRange(MediaTime.Zero, NonNegativeTimelineTime(SequenceDurationSeconds));
        if (probe is null || video is null || sourceDuration is not { } duration)
        {
            Block(PacketCopyBlocker.Media, "Source stream information is incomplete.");
            return PacketCopyDecision.Transcode(blockers, reasons);
        }
        var streamCopyInfo = CreateSegmentStreamCopyInfo(slice, preset);
        if (slice.SourceRange != new MediaRange(MediaTime.Zero, duration) &&
            CreateBoundaryGopRenderInfo(slice, preset) is not null)
        {
            forceBoundaryGop = true;
            reasons.Add("Only the cut GOPs will be encoded; untouched middle GOPs will be copied and the candidate validated.");
        }
        else if (slice.SourceRange != new MediaRange(MediaTime.Zero, duration) &&
                 IsMp4EditListPacketTrimSupported(preset, video.CodecName))
        {
            forceEditListStreamCopy = true;
            forceVideoStreamCopy = CanCopyTrimmedVideoPackets(slice, preset, streamCopyInfo);
            reasons.Add("MP4 edit-list trim will hide decode preroll while video and unchanged audio packets are copied.");
        }
        else if (slice.SourceRange != new MediaRange(MediaTime.Zero, duration) &&
                 CanCopyTrimmedVideoPackets(slice, preset, streamCopyInfo))
        {
            forceVideoStreamCopy = true;
            reasons.Add("Video boundaries are indexed keyframes; audio is rebuilt for exact timing.");
        }
        else if (slice.SourceRange != new MediaRange(MediaTime.Zero, duration))
        {
            Block(
                PacketCopyBlocker.SourceRange,
                EnableExperimentalBoundaryGopRendering
                    ? "This trim is not eligible for Boundary-GOP rendering; use CFR 8-bit H.264, VP9, or AV1 with at least one second of complete interior GOPs."
                    : "Trimmed clips still require encoding unless both edges are keyframes. Experimental Boundary-GOP rendering can be enabled in global settings.");
        }
        var sliceTimelineStart = clip.Model.SourceTimeToTimeline(slice.SourceRange.Start);
        var sliceTimelineDuration = clip.Model.SourceDurationToTimeline(slice.SourceRange.Duration);
        if (exportRange.Start != sliceTimelineStart || exportRange.Duration != sliceTimelineDuration)
        {
            Block(PacketCopyBlocker.TimelineRange, "The export range must match the clip boundaries without surrounding gaps.");
        }
        if (clip.PlaybackSpeedPercent != SequenceClip.DefaultPlaybackSpeedPercent)
        {
            Block(PacketCopyBlocker.ClipSpeed, "Clip playback speed is not 100%.");
        }
        if (video.RotationDegrees != 0)
        {
            Block(PacketCopyBlocker.SourceRotation, "Sources with rotation metadata are not yet packet-copy eligible.");
        }
        if (clip.CanvasTransform != ClipCanvasTransform.Identity)
        {
            Block(PacketCopyBlocker.Transform, "The clip has a move, scale, rotation, or mirror transform.");
        }
        if (CanvasSize != video.EncodedSize)
        {
            Block(PacketCopyBlocker.Canvas, "The project canvas does not match the encoded video size.");
        }
        if (CanvasCrop != CropRegion.FullFrame(CanvasSize))
        {
            Block(PacketCopyBlocker.Crop, "The crop does not cover the complete canvas.");
        }
        if (!SourceVideoCodecMatches(video.CodecName, preset.VideoCodec))
        {
            Block(PacketCopyBlocker.VideoCodec, "The selected output video codec differs from the source.");
        }
        if (preset.FrameRate is not null &&
            SelectedExportPreset.ParameterMode != ExportParameterMode.MatchInput)
        {
            Block(PacketCopyBlocker.FrameRate, "A fixed output frame rate is enabled.");
        }
        if (preset.SupportsAudio &&
            AudioTracks.Any(track => track.IsExternal && !track.IsMuted && !track.Edit.IsEmpty))
        {
            Block(PacketCopyBlocker.ExternalAudio, "Active external audio requires mixing.");
        }

        if (!preset.SupportsAudio)
        {
            return Finish();
        }

        var embedded = AudioTracks
            .Where(track =>
                !track.IsExternal &&
                !track.IsMuted &&
                track.EmbeddedLaneIndex is { } laneIndex &&
                clip.IncludesAudioLane(laneIndex) &&
                track.TryGetEmbeddedStreamIndex(clip.SourcePath, out _))
            .ToArray();
        if (embedded.Length == 0)
        {
            return Finish();
        }
        if (embedded.Length != 1 || embedded[0].EmbeddedLaneIndex is not { } embeddedLaneIndex)
        {
            Block(PacketCopyBlocker.AudioLayout, "Packet copy supports at most one active embedded audio track.");
            return Finish();
        }
        if (Math.Abs(CombineAudioGain(
                embedded[0].GainDb,
                clip.GetAudioLaneGainDb(embeddedLaneIndex))) >= 0.000_001)
        {
            Block(PacketCopyBlocker.AudioGain, "Audio gain changes require encoding.");
        }
        if (!embedded[0].CreateEditForClip(clip).IsUnedited)
        {
            Block(PacketCopyBlocker.AudioEdit, "Silenced or cut audio ranges require encoding.");
        }
        if (!embedded[0].TryGetEmbeddedStreamIndex(clip.SourcePath, out var streamIndex))
        {
            Block(PacketCopyBlocker.AudioLayout, "The embedded audio stream could not be mapped unchanged.");
            return Finish();
        }

        var audio = probe.AudioStreams.FirstOrDefault(stream => stream.Index == streamIndex);
        if (audio is null || !SourceAudioCodecMatches(audio.CodecName, preset.AudioCodec))
        {
            Block(PacketCopyBlocker.AudioCodec, "The selected output audio codec differs from the source.");
        }

        return Finish();
    }

    private static bool SourceVideoCodecMatches(string codecName, VideoCodecFamily codec) => codec switch
    {
        VideoCodecFamily.H264 => string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase),
        VideoCodecFamily.Vp9 => string.Equals(codecName, "vp9", StringComparison.OrdinalIgnoreCase),
        VideoCodecFamily.Av1 => string.Equals(codecName, "av1", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsMp4EditListPacketTrimSupported(
        ExportPreset preset,
        string sourceVideoCodec) =>
        preset.Container == ExportContainer.Mp4 &&
        preset.VideoCodec == VideoCodecFamily.H264 &&
        string.Equals(sourceVideoCodec, "h264", StringComparison.OrdinalIgnoreCase);

    private static bool SourceAudioCodecMatches(string codecName, AudioCodecFamily codec) => codec switch
    {
        AudioCodecFamily.Aac => string.Equals(codecName, "aac", StringComparison.OrdinalIgnoreCase),
        AudioCodecFamily.Opus => string.Equals(codecName, "opus", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public ExportPreset SelectedExportPreset
    {
        get => _selectedExportPreset;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedExportPreset, value))
            {
                OnPropertyChanged(nameof(CropSizeStep));
                OnPropertyChanged(nameof(IsCustomExport));
                OnPropertyChanged(nameof(IsGifExport));
                OnPropertyChanged(nameof(UsesCustomExportQuality));
                OnPropertyChanged(nameof(UsesMatchedInputQuality));
                RaiseExportStateChanged();
                MarkProjectDirty();
                if (ResolveMatchInput(GetSequenceExportSlices()) is { } resolution)
                {
                    StatusText = resolution.Explanation;
                }
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

            SynchronizeAudioGainTargets();

            OnPropertyChanged(nameof(CanDeleteSelectedVideoClip));
            OnPropertyChanged(nameof(PreviewAudioTracks));
            OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
            OnPropertyChanged(nameof(CanApplyCropPreset));
            OnPropertyChanged(nameof(CanMoveSelectedVideoLeft));
            OnPropertyChanged(nameof(CanMoveSelectedVideoRight));
            OnPropertyChanged(nameof(SelectedClipPlaybackSpeedPercent));
            RaiseExportStateChanged();
        }
    }

    public int SelectedClipPlaybackSpeedPercent
    {
        get => SelectedVideoClip?.PlaybackSpeedPercent ?? SequenceClip.DefaultPlaybackSpeedPercent;
        set => SetSelectedClipPlaybackSpeed(value);
    }

    public bool SetSelectedClipPlaybackSpeed(int playbackSpeedPercent)
    {
        if (SelectedVideoClip is not { } clip)
        {
            return false;
        }

        var next = Math.Clamp(
            playbackSpeedPercent,
            SequenceClip.MinimumPlaybackSpeedPercent,
            SequenceClip.MaximumPlaybackSpeedPercent);
        if (next == clip.PlaybackSpeedPercent)
        {
            return false;
        }

        var previousModel = clip.Model;
        var previousEnd = previousModel.TimelineEnd;
        var previousDuration = previousModel.Duration;
        var previousPlayhead = _sequencePlayhead;
        var previousSelectionStart = _sequenceSelectionStart;
        var previousSelectionEnd = _sequenceSelectionEnd;
        var laterClips = VideoClips
            .Where(candidate => !ReferenceEquals(candidate, clip) && candidate.TimelineStart >= previousEnd)
            .ToArray();
        var wasLoading = _isLoadingProject;
        _isLoadingProject = true;
        try
        {
            clip.PlaybackSpeedPercent = next;
            foreach (var audioTrack in AudioTracks)
            {
                audioTrack.RemapTimelineForClipSpeedChange(previousModel, clip.Model);
            }
            var shift = clip.Duration - previousDuration;
            foreach (var later in laterClips)
            {
                later.TimelineStart += shift;
            }

            _sequencePlayhead = RemapTimelineTimeForSpeedChange(
                previousPlayhead,
                previousModel,
                clip.Model);
            _sequenceSelectionStart = RemapTimelineTimeForSpeedChange(
                previousSelectionStart,
                previousModel,
                clip.Model);
            _sequenceSelectionEnd = RemapTimelineTimeForSpeedChange(
                previousSelectionEnd,
                previousModel,
                clip.Model);
        }
        finally
        {
            _isLoadingProject = wasLoading;
        }

        UpdateSequenceLayout(resetSelectionIfEmpty: false);
        RaiseSequenceSelectionChanged();
        SyncSourcePreviewToSequenceTime(_sequencePlayhead, selectClip: false);
        RefreshAudioTimelineSegments(refreshWaveforms: false);
        StartSequenceTimelineAnalysis(debounce: false);
        OnPropertyChanged(nameof(SelectedClipPlaybackSpeedPercent));
        StatusText = $"Set {clip.DisplayName} playback speed to {next}%";
        MarkProjectDirty($"clip:{clip.Id}:playback-speed");
        return true;
    }

    private static MediaTime RemapTimelineTimeForSpeedChange(
        MediaTime timelineTime,
        SequenceClip previous,
        SequenceClip current)
    {
        if (timelineTime <= previous.TimelineStart)
        {
            return timelineTime;
        }
        if (timelineTime < previous.TimelineEnd)
        {
            return current.SourceTimeToTimeline(previous.TimelineTimeToSource(timelineTime));
        }

        return timelineTime + (current.Duration - previous.Duration);
    }

    public double SequenceDurationSeconds =>
        VideoClips.Count == 0 ? 0 : VideoClips.Max(static clip => clip.TimelineEndSeconds);

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
            SynchronizeAudioTimelineState(refreshWaveforms: false);
        }
    }

    public string SequencePlayheadText => FormatSequenceTimestamp(_sequencePlayhead);

    public bool IsSequencePlayheadInGap
    {
        get => _isSequencePlayheadInGap;
        private set
        {
            if (SetProperty(ref _isSequencePlayheadInGap, value))
            {
                OnPropertyChanged(nameof(IsClipTransformOverlayActive));
                OnPropertyChanged(nameof(IsAutoCanvasOverlayActive));
            }
        }
    }

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

    public bool IsTimelineSnappingEnabled
    {
        get => _isTimelineSnappingEnabled;
        set
        {
            if (SetProperty(ref _isTimelineSnappingEnabled, value))
            {
                StatusText = value
                    ? "Timeline snapping enabled"
                    : "Timeline snapping disabled";
            }
        }
    }

    public bool MoveTimelineClipsByDefault
    {
        get => _moveTimelineClipsByDefault;
        set
        {
            if (SetProperty(ref _moveTimelineClipsByDefault, value))
            {
                OnPropertyChanged(nameof(TimelinePointerModeText));
                StatusText = value
                    ? "Timeline Move mode: drag clip bodies to reposition them"
                    : "Timeline Range mode: drag to select; Ctrl+drag moves a clip";
            }
        }
    }

    public string TimelinePointerModeText => MoveTimelineClipsByDefault ? "Move" : "Range";

    public bool IsSequenceTimelineFreeMode
    {
        get => _isSequenceTimelineFreeMode;
        set
        {
            if (!SetProperty(ref _isSequenceTimelineFreeMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SequenceTimelineModeText));
            FitSequenceTimeline();
        }
    }

    public string SequenceTimelineModeText => IsSequenceTimelineFreeMode ? "Free" : "Fit";

    public double SequenceTimelineZoom
    {
        get => _sequenceTimelineZoom;
        set
        {
            var next = TimelineViewportMath.ClampZoom(value, IsSequenceTimelineFreeMode);
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
            var next = TimelineViewportMath.ClampStart(SequenceDurationSeconds, SequenceTimelineZoom, value, IsSequenceTimelineFreeMode);
            if (SetProperty(ref _sequenceTimelineViewportStart, next))
            {
                RaiseSequenceViewportChanged();
                StartSequenceTimelineAnalysis(debounce: true);
            }
        }
    }

    public double SequenceTimelineViewportDuration =>
        TimelineViewportMath.VisibleDuration(SequenceDurationSeconds, SequenceTimelineZoom, IsSequenceTimelineFreeMode);

    public double SequenceTimelineViewportEnd =>
        SequenceTimelineViewportStart + SequenceTimelineViewportDuration;

    public string SequenceTimelineZoomText => $"{SequenceTimelineZoom:0.#}×";

    public string SequenceTimelineViewportText =>
        $"{FormatTimelineViewportPosition(SequenceTimelineViewportStart)} – " +
        FormatTimelineViewportPosition(SequenceTimelineViewportEnd);

    public bool CanZoomSequenceTimelineIn => SequenceTimelineZoom < TimelineViewportMath.MaximumZoom;

    public bool CanZoomSequenceTimelineOut =>
        SequenceTimelineZoom > (IsSequenceTimelineFreeMode ? TimelineViewportMath.MinimumFreeZoom : 1);

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
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
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

            var preset = ResolveSelectedExportPreset(slices);
            var outputSize = CurrentExportEncodingSettings.CalculateOutputSize(
                CanvasCrop.ExportSize,
                preset.RequiresEvenDimensions);
            if (preset.RequiresEvenDimensions &&
                (((outputSize.Width & 1) != 0) || ((outputSize.Height & 1) != 0)))
            {
                return $"{preset.DisplayName} requires even output dimensions; current output is {outputSize.Width} × {outputSize.Height}";
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

            var preset = ResolveSelectedExportPreset(slices);
            var outputSize = CurrentExportEncodingSettings.CalculateOutputSize(
                CanvasCrop.ExportSize,
                preset.RequiresEvenDimensions);
            var duration = HasSequenceSelection
                ? NormalizedSequenceSelection().Duration
                : NonNegativeTimelineTime(SequenceDurationSeconds);
            duration = CurrentExportEncodingSettings.ApplyPlaybackSpeed(duration);
            var gifDetails = preset.VideoCodec == VideoCodecFamily.Gif
                ? $" · {GifFrameRate} fps"
                : string.Empty;
            var quality = IsGifExport ||
                          ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.Custom
                ? $"quality {ExportQuality}%"
                : "match input quality";
            var strategy = ResolveExportStrategy(slices, preset) switch
            {
                ExportStrategy.StreamCopy => "packet copy · no re-encode",
                ExportStrategy.EditListStreamCopy => "MP4 packet trim · no re-encode",
                ExportStrategy.ConcatStreamCopy => "packet-copy join · no re-encode",
                ExportStrategy.VideoStreamCopy => "video copy · audio re-encode",
                ExportStrategy.BoundaryGop => "experimental Boundary-GOP · validated",
                _ => "exact sequence re-encode",
            };
            return $"{preset.DisplayName} · {strategy} · " +
                   $"{outputSize.Width} × {outputSize.Height} · {quality}{gifDetails} · " +
                   FormatSequenceTimestamp(duration);
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
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void ReportUpdateStatus() => StatusText = Updates.StatusText;

    public bool HasReadyMedia => MediaItems.Any(item => item.IsReady);

    public bool ShowEmptyState => !HasReadyMedia;

    public bool ShowQuickWorkspace => HasReadyMedia;

    public bool ShowTimeline => VideoItems.Any();

    public bool HasAudioTracks => AudioTracks.Count > 0;

    public bool CanRestoreMissingAudioTracks => MediaItems.Any(item =>
    {
        if (item.Media is null)
        {
            return false;
        }

        return item.Media.Probe.AudioStreams.Select((stream, laneIndex) => (stream, laneIndex)).Any(entry =>
            item.IsExternalAudio
                ? !AudioTracks.Any(track =>
                    track.IsExternal &&
                    PathComparer.Equals(track.SourcePath, item.SourcePath) &&
                    track.StreamIndex == entry.stream.Index)
                : !AudioTracks.Any(track =>
                    !track.IsExternal &&
                    track.EmbeddedLaneIndex == entry.laneIndex &&
                    track.HasEmbeddedSource(item.SourcePath, entry.stream.Index)));
    });

    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (!SetProperty(ref _isAdvancedMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowAdvancedClipControls));
            OnPropertyChanged(nameof(ShowAudioMixer));
            OnPropertyChanged(nameof(AudioMixerButtonText));
            StatusText = value ? "Advanced controls shown" : "Advanced controls hidden";
            if (ShowAudioMixer)
            {
                RefreshAudioTimelineSegments(refreshWaveforms: false);
                foreach (var track in AudioTracks)
                {
                    StartWaveformAnalysis(track, debounce: false);
                }
            }
        }
    }

    public bool ShowAdvancedClipControls => IsAdvancedMode && HasReadyMedia;

    public bool ShowAudioMixer => HasAudioTracks && IsAdvancedMode;

    public bool ShowRangeStrip => false;

    public IEnumerable<MediaItemViewModel> VideoItems => MediaItems.Where(item => item.HasVideo);

    public IEnumerable<MediaItemViewModel> ExternalAudioItems => MediaItems.Where(item => item.IsExternalAudio);

    public string AudioTrackCountText =>
        $"{AudioTracks.Count} track{(AudioTracks.Count == 1 ? string.Empty : "s")}";

    public IReadOnlyList<PreviewAudioTrack> PreviewAudioTracks => CreatePreviewAudioTracks();

    public string AudioMixerButtonText => IsAdvancedMode ? "Basic" : "Advanced";

    private IReadOnlyList<PreviewAudioTrack> CreatePreviewAudioTracks()
    {
        if (SelectedMedia is null)
        {
            return [];
        }

        var previewTracks = new List<PreviewAudioTrack>();
        foreach (var track in AudioTracks)
        {
            if (track.IsExternal)
            {
                previewTracks.Add(new PreviewAudioTrack(
                    track.SourcePath,
                    track.StreamIndex,
                    track.GainDb,
                    track.IsMuted || track.Edit.IsEmpty,
                    track.TimelineOffset,
                    track.Edit));
                continue;
            }

            if (SelectedVideoClip is not { } clip ||
                !ReferenceEquals(clip.Source, SelectedMedia) ||
                track.EmbeddedLaneIndex is not { } laneIndex ||
                !clip.IncludesAudioLane(laneIndex) ||
                !track.TryGetEmbeddedStreamIndex(clip.SourcePath, out var streamIndex))
            {
                continue;
            }

            var edit = track.CreateEditForClip(clip);
            previewTracks.Add(new PreviewAudioTrack(
                streamIndex,
                CombineAudioGain(track.GainDb, clip.GetAudioLaneGainDb(laneIndex)),
                track.IsMuted || edit.IsEmpty,
                edit));
        }

        return previewTracks;
    }

    public string EditingModeText => ShowTimeline ? "TIMELINE" : "QUICK EDIT";

    public string CropSizeText
    {
        get => VideoClips.Count == 0 ? "No video selected" : CanvasCropSizeText;
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
            StartKeyframeIndexing(item);
            AddAudioTracks(item);
            if (!_isLoadingProject && item is { IsReady: true, HasVideo: true })
            {
                AddInitialVideoClip(item);
            }
            if (!_isLoadingProject && item.IsReady && SelectedMedia is null)
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

    public bool AddMediaToTimeline(MediaItemViewModel? mediaItem = null)
    {
        var source = mediaItem ?? SelectedMedia;
        if (source is not { IsReady: true, HasVideo: true })
        {
            StatusText = "Select a ready video in Media first.";
            return false;
        }

        var duration = source.Edit?.SourceDuration ?? source.Media?.Probe.Duration;
        if (duration is null || duration <= MediaTime.Zero)
        {
            StatusText = $"{source.DisplayName} has no usable video duration.";
            return false;
        }

        var previous = VideoClips.LastOrDefault(clip => ReferenceEquals(clip.Source, source));
        var fullRange = new MediaRange(MediaTime.Zero, duration.Value);
        var defaultTransform = CanvasSize == new PixelSize(1, 1)
            ? ClipCanvasTransform.Identity
            : ClipCanvasTransform.Fill(source.VideoSize, CanvasSize);
        var clip = AddVideoClipInstance(
            source,
            fullRange,
            fullRange,
            previous?.SourceWindow ?? source.Crop,
            previous?.CanvasTransform ?? defaultTransform,
            audioGainDb: 0,
            NonNegativeTimelineTime(SequenceDurationSeconds),
            selectClip: true,
            collapseSelection: true);
        StatusText = $"Added another {clip.DisplayName} clip to the timeline";
        MarkProjectDirty();
        return true;
    }

    public bool CopySelectedVideoClip()
    {
        if (SelectedVideoClip is not { } clip)
        {
            StatusText = "Select a timeline clip to copy.";
            return false;
        }

        _videoClipClipboard = new VideoClipClipboard(
            clip.Source.Id,
            clip.Model.SourceRange,
            clip.Model.AvailableRange,
            clip.SourceWindow,
            clip.CanvasTransform,
            clip.AudioGainDb,
            clip.PlaybackSpeedPercent,
            clip.ExcludedAudioLaneIndices.ToImmutableArray(),
            clip.AudioLaneGainDb.ToImmutableDictionary());
        StatusText = $"Copied {clip.DisplayName}; press Ctrl+V on the timeline to paste";
        return true;
    }

    public bool PasteVideoClip()
    {
        if (_videoClipClipboard is not { } copied)
        {
            StatusText = "Copy a timeline clip before pasting.";
            return false;
        }

        var source = MediaItems.FirstOrDefault(item => item.Id == copied.SourceId && item.IsReady && item.HasVideo);
        if (source is null)
        {
            _videoClipClipboard = null;
            StatusText = "The copied clip's source is no longer in this project.";
            return false;
        }

        var preferredStart = SelectedVideoClip?.TimelineEnd ?? NonNegativeTimelineTime(SequenceDurationSeconds);
        var copiedDuration = copied.SourceRange.Duration * 100 / copied.PlaybackSpeedPercent;
        var timelineStart = FindAvailableTimelineStart(preferredStart, copiedDuration);
        var clip = AddVideoClipInstance(
            source,
            copied.SourceRange,
            copied.AvailableRange,
            copied.SourceWindow,
            copied.CanvasTransform,
            copied.AudioGainDb,
            timelineStart,
            selectClip: true,
            collapseSelection: true,
            copied.ExcludedAudioLaneIndices,
            copied.PlaybackSpeedPercent,
            copied.AudioLaneGainDb);
        StatusText = $"Pasted {clip.DisplayName} at {FormatSequenceTimestamp(timelineStart)}";
        MarkProjectDirty();
        return true;
    }

    public string GetSuggestedExportFileName()
    {
        var sourceName = SelectedMedia is null
            ? "clip"
            : Path.GetFileNameWithoutExtension(SelectedMedia.DisplayName);
        return $"{sourceName}-clip{GetEffectiveExportPreset().FileExtension}";
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

        var exportPreset = ResolveSelectedExportPreset(slices);
        var exportStrategy = ResolveExportStrategy(slices, exportPreset);
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
                        exportPreset.SupportsAudio &&
                        !track.IsExternal &&
                        !track.IsMuted &&
                        track.EmbeddedLaneIndex is { } laneIndex &&
                        slice.Clip.IncludesAudioLane(laneIndex) &&
                        track.TryGetEmbeddedStreamIndex(slice.Clip.SourcePath, out _))
                    .Select(track =>
                    {
                        track.TryGetEmbeddedStreamIndex(slice.Clip.SourcePath, out var streamIndex);
                        var edit = track.CreateEditForClip(slice.Clip);
                        return new ExportAudioTrackPlan(
                            streamIndex,
                            CombineAudioGain(
                                track.GainDb,
                                slice.Clip.GetAudioLaneGainDb(track.EmbeddedLaneIndex!.Value)),
                            edit);
                    })
                    .Where(plan => plan.AudioEdit is { IsEmpty: false })
                    .ToImmutableArray();
                return new ExportVideoSegmentPlan(
                    slice.Clip.SourcePath,
                    video.Index,
                    slice.SourceRange,
                    CanvasSize,
                    CanvasCrop,
                    slice.Clip.CanvasTransform,
                    embeddedAudio,
                    slice.Clip.Model.SourceTimeToTimeline(slice.SourceRange.Start),
                    slice.Clip.PlaybackSpeedPercent,
                    new ExportVideoColorInfo(
                        video.PixelFormat,
                        video.ColorRange,
                        video.ColorSpace,
                        video.ColorTransfer,
                        video.ColorPrimaries),
                    isCompleteSource: slice.SourceRange.Start == MediaTime.Zero &&
                                      slice.SourceRange.End ==
                                      (slice.Clip.Source.Edit?.SourceDuration ??
                                       slice.Clip.Source.Media!.Probe.Duration),
                    sourceSize: video.OrientedSize,
                    streamCopyInfo: CreateSegmentStreamCopyInfo(slice, exportPreset),
                    boundaryGopInfo: exportStrategy == ExportStrategy.BoundaryGop
                        ? CreateBoundaryGopRenderInfo(slice, exportPreset)
                        : null);
            }).ToImmutableArray();
            var externalAudio = AudioTracks
                .Where(track =>
                    exportPreset.SupportsAudio &&
                    track.IsExternal &&
                    !track.IsMuted &&
                    !track.Edit.IsEmpty)
                .Select(track => new ExportAudioTrackPlan(
                    track.SourcePath,
                    track.StreamIndex,
                    track.GainDb,
                    track.TimelineOffset,
                    track.Edit))
                .ToImmutableArray();
            var exportRange = HasSequenceSelection
                ? NormalizedSequenceSelection()
                : new MediaRange(MediaTime.Zero, NonNegativeTimelineTime(SequenceDurationSeconds));
            var selectionStart = exportRange.Start;
            var plan = new ExportPlan(
                videoSegments,
                CanvasCrop.ExportSize,
                destinationPath,
                exportPreset,
                replaceExistingDestination,
                externalAudio,
                selectionStart,
                exportRange.Duration,
                CurrentExportEncodingSettings,
                exportStrategy);
            IsExporting = true;
            ExportProgress = 0;
            ExportPhaseText = "Preparing";
            StatusText = ExportPlanSummary;
            var progress = new Progress<ClipEdit.Media.Export.ExportProgress>(update =>
            {
                ExportProgress = update.Fraction;
                ExportPhaseText = FormatExportProgress(update, ExportProgressPercent);
            });

            var result = await _exportRenderer.RenderAsync(plan, progress, request.Token);
            ExportProgress = 1;
            var usedBoundaryFallback = exportStrategy == ExportStrategy.BoundaryGop &&
                                       result.ActualStrategy == ExportStrategy.ExactTranscode;
            var usedEncoderFallback = PreferredExportVideoEncoder != ExportVideoEncoder.Software &&
                                      result.ActualVideoEncoder == ExportVideoEncoder.Software;
            ExportPhaseText = usedBoundaryFallback
                ? "Complete · exact fallback · 100%"
                : usedEncoderFallback
                    ? "Complete · software fallback · 100%"
                : "Complete · 100%";
            StatusText = usedBoundaryFallback
                ? $"Exported {Path.GetFileName(result.DestinationPath)} using exact fallback after Boundary-GOP validation failed"
                : usedEncoderFallback
                    ? $"Exported {Path.GetFileName(result.DestinationPath)} after the hardware encoder failed and software encoding succeeded"
                : $"Exported {Path.GetFileName(result.DestinationPath)}";
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
        IsAdvancedMode = !IsAdvancedMode;
    }

    public void MarkSequenceSelectionStart()
    {
        _sequenceSelectionStart = SnapTimelineCutIfEnabled(_sequencePlayhead);
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
        _sequenceSelectionEnd = SnapTimelineCutIfEnabled(_sequencePlayhead);
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
                if (clip.TimelineStart >= selection.End)
                {
                    replacements.Add(clip.CreateSibling(
                        clip.Model.MoveTo(clip.TimelineStart - selection.Duration)));
                }
                else
                {
                    replacements.Add(clip);
                }

                continue;
            }

            var sourceRemoval = new MediaRange(
                clip.Model.TimelineTimeToSource(overlapStart),
                clip.Model.TimelineTimeToSource(overlapEnd));
            foreach (var part in clip.Model.Remove(sourceRemoval, Guid.NewGuid()))
            {
                var timelineStart = part.TimelineStart >= selection.End
                    ? part.TimelineStart - selection.Duration
                    : part.TimelineStart;
                replacements.Add(clip.CreateSibling(
                    part.MoveTo(timelineStart < MediaTime.Zero
                        ? MediaTime.Zero
                        : timelineStart)));
            }
        }

        ReplaceVideoClips(replacements, preferredClipId: null);
        CollapseSequenceSelection(selection.Start);
        StatusText = "Removed the selected timeline range and ripple-closed the gap; source handles remain recoverable";
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

        var timelineRemovals = new List<MediaRange>();
        foreach (var clip in VideoClips)
        {
            var overlapStart = Max(selection.Start, clip.TimelineStart);
            var overlapEnd = Min(selection.End, clip.TimelineEnd);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            if (clip.TimelineStart < overlapStart)
            {
                timelineRemovals.Add(new MediaRange(clip.TimelineStart, overlapStart));
            }

            if (overlapEnd < clip.TimelineEnd)
            {
                timelineRemovals.Add(new MediaRange(overlapEnd, clip.TimelineEnd));
            }
        }

        var mergedRemovals = MergeTimelineRanges(timelineRemovals);
        MediaTime RippleTime(MediaTime time)
        {
            var removed = MediaTime.Zero;
            foreach (var range in mergedRemovals)
            {
                if (range.Start >= time)
                {
                    break;
                }

                var removedEnd = Min(range.End, time);
                if (removedEnd > range.Start)
                {
                    removed += removedEnd - range.Start;
                }
            }

            var rippled = time - removed;
            return rippled < MediaTime.Zero ? MediaTime.Zero : rippled;
        }

        var replacements = new List<VideoClipViewModel>(VideoClips.Count);
        Guid? firstTouchedClipId = null;
        Guid? selectedTouchedClipId = null;
        foreach (var clip in VideoClips)
        {
            var overlapStart = Max(selection.Start, clip.TimelineStart);
            var overlapEnd = Min(selection.End, clip.TimelineEnd);
            if (overlapEnd <= overlapStart)
            {
                var moved = clip.Model.MoveTo(RippleTime(clip.TimelineStart));
                replacements.Add(ReferenceEquals(moved, clip.Model)
                    ? clip
                    : clip.CreateSibling(moved));
                continue;
            }

            var sourceSelection = new MediaRange(
                clip.Model.TimelineTimeToSource(overlapStart),
                clip.Model.TimelineTimeToSource(overlapEnd));
            if (clip.Model.KeepOnly(sourceSelection) is { } kept)
            {
                firstTouchedClipId ??= clip.Id;
                if (clip.Id == SelectedVideoClip?.Id)
                {
                    selectedTouchedClipId = clip.Id;
                }

                replacements.Add(clip.CreateSibling(
                    kept.MoveTo(RippleTime(overlapStart))));
            }
        }

        var preferredClipId = selectedTouchedClipId ?? firstTouchedClipId;
        if (preferredClipId is null)
        {
            return false;
        }

        var collapsedAt = replacements.First(clip => clip.Id == preferredClipId).TimelineStart;
        ReplaceVideoClips(replacements, preferredClipId);
        CollapseSequenceSelection(collapsedAt);
        StatusText = "Kept the selected content, closed the trimmed gaps, and selected the remaining clip";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    private static IReadOnlyList<MediaRange> MergeTimelineRanges(IEnumerable<MediaRange> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        if (ordered.Length < 2)
        {
            return ordered;
        }

        var merged = new List<MediaRange>(ordered.Length);
        var current = ordered[0];
        foreach (var next in ordered.Skip(1))
        {
            if (next.Start <= current.End)
            {
                current = new MediaRange(current.Start, Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    public bool SplitSelectedVideoClip()
    {
        var clip = FindClipAtTimelineTime(_sequencePlayhead) ?? SelectedVideoClip;
        if (clip is null || _sequencePlayhead <= clip.TimelineStart || _sequencePlayhead >= clip.TimelineEnd)
        {
            return false;
        }

        var splitTime = SnapTimelineCutIfEnabled(_sequencePlayhead, clip);
        var sourceTime = clip.Model.TimelineTimeToSource(splitTime);
        var (left, right) = clip.Model.Split(sourceTime, Guid.NewGuid());
        var index = VideoClips.IndexOf(clip);
        var replacements = VideoClips.ToList();
        replacements.RemoveAt(index);
        replacements.Insert(index, clip.CreateSibling(left));
        replacements.Insert(index + 1, clip.CreateSibling(right));
        ReplaceVideoClips(replacements, right.Id);
        _sequencePlayhead = splitTime;
        OnPropertyChanged(nameof(SequencePlayheadSeconds));
        OnPropertyChanged(nameof(SequencePlayheadText));
        CollapseSequenceSelection(splitTime);
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
            .ToDictionary(group => group.Key, group => group.First().CanvasTransform);
        var replacements = new List<VideoClipViewModel>(videoSources.Length);
        var timelineCursor = MediaTime.Zero;
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
                new SequenceClip(Guid.NewGuid(), source.Id, fullRange, fullRange, timelineCursor),
                source.Crop,
                placements.GetValueOrDefault(source.Id, ClipCanvasTransform.Fill(source.VideoSize, CanvasSize))));
            timelineCursor += fullRange.Duration;
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
            anchor ?? SequencePlayheadSeconds,
            IsSequenceTimelineFreeMode);
        _sequenceTimelineZoom = result.Zoom;
        _sequenceTimelineViewportStart = result.Start;
        RaiseSequenceViewportChanged();
        StartSequenceTimelineAnalysis(debounce: true);
    }

    public void FitSequenceTimeline()
    {
        _sequenceTimelineZoom = 1;
        _sequenceTimelineViewportStart = TimelineViewportMath.ClampStart(SequenceDurationSeconds, 1, 0, IsSequenceTimelineFreeMode);
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
        return ResetSelectedClipToFill();
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
            var proposedCrop = SelectedCropAspectPreset.IsFullFrame
                ? CropRegion.FullFrame(CanvasSize)
                : CanvasCrop.ResizeToAspectRatio(
                    SelectedCropAspectPreset.WidthUnits,
                    SelectedCropAspectPreset.HeightUnits);
            CanvasCrop = SnapCropPresetSizeCentered(proposedCrop, SelectedCropAspectPreset);
        }
        finally
        {
            _isApplyingCropPreset = false;
        }

        StatusText = $"Applied {SelectedCropAspectPreset.DisplayName} to the one shared crop frame";
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
        RepackVideoClips();
        UpdateSequenceLayout(resetSelectionIfEmpty: false);
        SelectedVideoClip = source;
        StatusText = $"Moved {source.DisplayName} in the video sequence";
        MarkProjectDirty();
        StartSequenceTimelineAnalysis(debounce: false);
        return true;
    }

    public bool MoveVideoClipTo(VideoClipViewModel clip, double requestedTimelineStart)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!VideoClips.Contains(clip) || IsBusy || IsExporting || !double.IsFinite(requestedTimelineStart))
        {
            return false;
        }

        var requested = Math.Max(0, requestedTimelineStart);
        var previousDuration = SequenceDurationSeconds;
        var selectionCoveredWholeSequence = _sequenceSelectionStart == MediaTime.Zero &&
                                            _sequenceSelectionEnd == SequenceTimeFromSeconds(previousDuration);
        var duration = clip.DurationSeconds;
        var others = VideoClips
            .Where(candidate => !ReferenceEquals(candidate, clip))
            .OrderBy(candidate => candidate.TimelineStartSeconds)
            .ToArray();
        var overlapsOccupiedTime = others.Any(other =>
            requested + duration > other.TimelineStartSeconds + 0.000001 &&
            requested < other.TimelineEndSeconds - 0.000001);
        var timelineStart = NonNegativeTimelineTime(overlapsOccupiedTime
            ? InsertClipAtRequestedPosition(requested, duration, others)
            : requested);
        if (timelineStart == clip.TimelineStart && !overlapsOccupiedTime)
        {
            return false;
        }

        clip.TimelineStart = timelineStart;
        var ordered = VideoClips
            .OrderBy(candidate => candidate.TimelineStart)
            .ThenBy(candidate => candidate.Id)
            .ToArray();
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var sourceIndex = VideoClips.IndexOf(ordered[targetIndex]);
            if (sourceIndex != targetIndex)
            {
                VideoClips.Move(sourceIndex, targetIndex);
            }
        }

        UpdateSequenceLayout(resetSelectionIfEmpty: false);
        if (selectionCoveredWholeSequence)
        {
            _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
            RaiseSequenceSelectionChanged();
        }

        SyncSourcePreviewToSequenceTime(_sequencePlayhead, selectClip: false);
        SelectedVideoClip = clip;
        StatusText = $"Moved {clip.DisplayName} to {FormatSequenceTimestamp(timelineStart)}";
        return true;
    }

    public bool TryAdvanceSequencePlayback()
    {
        if (SelectedVideoClip is not { } currentClip)
        {
            return false;
        }

        var ordered = VideoClips
            .OrderBy(clip => clip.TimelineStart)
            .ThenBy(clip => clip.Id)
            .ToArray();
        var currentIndex = Array.IndexOf(ordered, currentClip);
        if (currentIndex < 0 || currentIndex >= ordered.Length - 1)
        {
            return false;
        }

        var nextClip = ordered[currentIndex + 1];
        SetSequencePlaybackPosition(nextClip.TimelineStart);
        StatusText = $"Playing next clip: {nextClip.DisplayName}";
        return true;
    }

    public MediaTime? PrepareSequencePlayback()
    {
        var ordered = VideoClips
            .OrderBy(clip => clip.TimelineStart)
            .ThenBy(clip => clip.Id)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        VideoClipViewModel? clip;
        if (_sequencePlayhead >= ordered[^1].TimelineEnd)
        {
            clip = ordered[0];
            SetSequencePlaybackPosition(clip.TimelineStart);
        }
        else
        {
            clip = FindClipAtTimelineTime(_sequencePlayhead);
            if (clip is null)
            {
                clip = ordered.FirstOrDefault(candidate => candidate.TimelineStart > _sequencePlayhead);
                if (clip is null)
                {
                    return null;
                }

                SetSequencePlaybackPosition(clip.TimelineStart);
            }
            else
            {
                SelectedVideoClip = clip;
            }
        }

        var offset = Min(clip.Duration, Max(MediaTime.Zero, _sequencePlayhead - clip.TimelineStart));
        var sourcePosition = Min(
            clip.SourceEnd,
            clip.SourceStart + clip.Model.TimelineDurationToSource(offset));
        clip.Source.Playhead = sourcePosition;
        return sourcePosition;
    }

    private void SetSequencePlaybackPosition(MediaTime timelinePosition)
    {
        _sequencePlayhead = timelinePosition;
        OnPropertyChanged(nameof(SequencePlayheadSeconds));
        OnPropertyChanged(nameof(SequencePlayheadText));
        OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
        SyncSourcePreviewToSequenceTime(_sequencePlayhead, selectClip: true);
        SynchronizeAudioTimelineState(refreshWaveforms: false);
    }

    private static double InsertClipAtRequestedPosition(
        double requestedStart,
        double clipDuration,
        IReadOnlyList<VideoClipViewModel> orderedOtherClips)
    {
        var requestedCenter = requestedStart + (clipDuration / 2);
        var insertionIndex = 0;
        while (insertionIndex < orderedOtherClips.Count)
        {
            var candidate = orderedOtherClips[insertionIndex];
            var candidateCenter = candidate.TimelineStartSeconds + (candidate.DurationSeconds / 2);
            if (requestedCenter <= candidateCenter)
            {
                break;
            }

            insertionIndex++;
        }

        var insertionStart = insertionIndex == 0
            ? Math.Max(0, Math.Min(requestedStart, orderedOtherClips[0].TimelineStartSeconds))
            : orderedOtherClips[insertionIndex - 1].TimelineEndSeconds;
        var cursor = insertionStart + clipDuration;
        for (var index = insertionIndex; index < orderedOtherClips.Count; index++)
        {
            var candidate = orderedOtherClips[index];
            if (candidate.TimelineStartSeconds < cursor - 0.000001)
            {
                candidate.TimelineStart = NonNegativeTimelineTime(cursor);
            }

            cursor = Math.Max(cursor, candidate.TimelineEndSeconds);
        }

        return insertionStart;
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
            _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
            _isCropAspectLocked = false;
            ResetProjectCanvasState();
            if (!RememberExportAdjustments)
            {
                ResetTransientExportAdjustments();
            }
            OnPropertyChanged(nameof(SelectedCropAspectPreset));
            OnPropertyChanged(nameof(IsCropAspectLocked));
            IsProjectDirty = false;
            ResetEditHistory();
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
        foreach (var audioTrack in AudioTracks.ToArray())
        {
            if (audioTrack.IsExternal)
            {
                if (PathComparer.Equals(audioTrack.SourcePath, mediaItem.SourcePath))
                {
                    RemoveAudioTrackCore(audioTrack);
                }
                continue;
            }

            if (audioTrack.RemoveEmbeddedSource(mediaItem.SourcePath) &&
                audioTrack.EmbeddedSourcePaths.Count == 0)
            {
                RemoveAudioTrackCore(audioTrack);
            }
        }

        MediaItems.Remove(mediaItem);
        CancelKeyframeIndexing(mediaItem);
        mediaItem.Dispose();
        _knownPaths.Remove(mediaItem.SourcePath);
        if (_videoClipClipboard?.SourceId == mediaItem.Id)
        {
            _videoClipClipboard = null;
        }
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

    public bool RemoveAudioTrack(AudioTrackViewModel? track)
    {
        if (track is null || !AudioTracks.Contains(track) || IsBusy || IsExporting)
        {
            return false;
        }

        RemoveAudioTrackCore(track);
        RefreshAudioTimelineSegments(refreshWaveforms: ShowAudioMixer);
        StatusText = $"Removed {track.DisplayName} from the project mix";
        MarkProjectDirty();
        RaiseWorkspaceStateChanged();
        return true;
    }

    public bool RestoreMissingAudioTracks()
    {
        if (!CanRestoreMissingAudioTracks)
        {
            return false;
        }

        foreach (var item in MediaItems.Where(item => item.Media is not null))
        {
            AddAudioTracks(item);
        }
        StatusText = "Restored available source audio to the project mix";
        MarkProjectDirty();
        RaiseWorkspaceStateChanged();
        return true;
    }

    public bool ToggleSelectedClipAudioMembership(AudioTrackViewModel? track)
    {
        if (track is null ||
            track.IsExternal ||
            track.EmbeddedLaneIndex is not { } laneIndex ||
            SelectedVideoClip is not { } clip ||
            !track.HasEmbeddedSource(clip.SourcePath))
        {
            return false;
        }

        var include = !clip.IncludesAudioLane(laneIndex);
        if (!clip.SetAudioLaneIncluded(laneIndex, include))
        {
            return false;
        }

        RefreshAudioTimelineSegments(refreshWaveforms: ShowAudioMixer);
        SynchronizeAudioGainTargets();
        OnPropertyChanged(nameof(PreviewAudioTracks));
        RaiseExportStateChanged();
        StatusText = include
            ? $"Added {clip.DisplayName} audio to {track.DisplayName}"
            : $"Removed {clip.DisplayName} audio from {track.DisplayName}";
        MarkProjectDirty();
        return true;
    }

    private void RemoveAudioTrackCore(AudioTrackViewModel track)
    {
        track.PropertyChanged -= OnAudioTrackPropertyChanged;
        CancelWaveformAnalysis(track);
        track.Dispose();
        AudioTracks.Remove(track);
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
            SynchronizeEditHistoryBaseline();
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

    public async Task DiscoverRecoveryCandidatesAsync(CancellationToken cancellationToken = default)
    {
        RecoveryCandidates.Clear();
        if (_projectStore is null ||
            string.IsNullOrWhiteSpace(_recoveryDirectory) ||
            !Directory.Exists(_recoveryDirectory))
        {
            RaiseRecoveryStateChanged();
            return;
        }

        var pruneResult = await PruneRecoveryFilesAsync(cancellationToken);

        string[] recoveryPaths;
        try
        {
            recoveryPaths = Directory
                .EnumerateFiles(_recoveryDirectory, "*.recovery.clipedit", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Recovery files could not be inspected: {exception.Message}";
            RaiseRecoveryStateChanged();
            return;
        }

        foreach (var recoveryPath in recoveryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastModified = new DateTimeOffset(
                File.GetLastWriteTimeUtc(recoveryPath),
                TimeSpan.Zero);
            try
            {
                var document = await _projectStore.LoadAsync(recoveryPath, cancellationToken);
                var referencedMedia = document.Media
                    .Select(media =>
                    {
                        var name = Path.GetFileName(media.SourcePath);
                        return string.IsNullOrWhiteSpace(name) ? media.SourcePath : name;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                RecoveryCandidates.Add(new RecoveryCandidateViewModel(
                    recoveryPath,
                    document.ProjectId,
                    lastModified,
                    referencedMedia));
            }
            catch (ProjectStoreException exception)
            {
                RecoveryCandidates.Add(new RecoveryCandidateViewModel(
                    recoveryPath,
                    Guid.Empty,
                    lastModified,
                    [],
                    exception.Message));
            }
        }

        if (RecoveryCandidates.Count > 0)
        {
            StatusText = $"{RecoveryCandidates.Count} recovery autosave{(RecoveryCandidates.Count == 1 ? string.Empty : "s")} available";
        }
        else if (pruneResult.DeletedFiles > 0)
        {
            StatusText = $"Removed {pruneResult.DeletedFiles} expired recovery file{(pruneResult.DeletedFiles == 1 ? string.Empty : "s")}";
        }

        if (pruneResult.FailedFiles > 0)
        {
            StatusText = $"{StatusText} · {pruneResult.FailedFiles} recovery file{(pruneResult.FailedFiles == 1 ? string.Empty : "s")} could not be cleaned up";
        }

        RaiseRecoveryStateChanged();
    }

    public async Task<bool> DiscardRecoveryAsync(
        RecoveryCandidateViewModel candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_projectStore is null)
        {
            return false;
        }

        try
        {
            await _projectStore.DeleteIfExistsAsync(candidate.RecoveryPath, cancellationToken);
            RecoveryCandidates.Remove(candidate);
            StatusText = "Discarded the recovery autosave; source media was not changed";
            RaiseRecoveryStateChanged();
            return true;
        }
        catch (ProjectStoreException exception)
        {
            StatusText = exception.Message;
            return false;
        }
    }

    public async Task<bool> OpenProjectWithRelinkingAsync(
        string projectPath,
        bool isRecovery = false,
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

        var unavailableMedia = document.Media
            .Select(media => (media, reason: GetUnavailableMediaReason(media)))
            .Where(entry => entry.reason is not null)
            .ToArray();
        if (unavailableMedia.Length == 0)
        {
            var opened = isRecovery
                ? await RecoverProjectAsync(projectPath, cancellationToken)
                : await OpenProjectAsync(projectPath, discardUnsavedChanges, cancellationToken);
            if (opened && isRecovery)
            {
                var recoveredCandidate = RecoveryCandidates.FirstOrDefault(candidate =>
                    PathComparer.Equals(candidate.RecoveryPath, Path.GetFullPath(projectPath)));
                if (recoveredCandidate is not null)
                {
                    RecoveryCandidates.Remove(recoveredCandidate);
                    RaiseRecoveryStateChanged();
                }
            }

            return opened;
        }

        var fullProjectPath = Path.GetFullPath(projectPath);
        var missing = await Task.Run(() =>
        {
            var suggestedPaths = new HashSet<string>(PathComparer);
            return unavailableMedia
                .Select(entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var suggestion = MissingMediaSuggestionFinder.FindSuggestion(
                        fullProjectPath,
                        entry.media,
                        suggestedPaths,
                        cancellationToken);
                    if (suggestion is not null)
                    {
                        suggestedPaths.Add(suggestion);
                    }

                    return new MissingMediaReferenceViewModel(
                        entry.media,
                        entry.reason!,
                        suggestion);
                })
                .ToArray();
        }, cancellationToken);

        ClearPendingProjectOpen();
        _pendingProjectDocument = document;
        _pendingProjectPath = fullProjectPath;
        _pendingProjectIsRecovery = isRecovery;
        _pendingProjectDiscardUnsavedChanges = discardUnsavedChanges;
        foreach (var reference in missing)
        {
            MissingMediaReferences.Add(reference);
        }

        var suggestionCount = missing.Count(reference => reference.HasSuggestion);
        StatusText = suggestionCount == 0
            ? $"{missing.Length} media file{(missing.Length == 1 ? " needs" : "s need")} relinking before the project can open"
            : $"Found {suggestionCount} nearby relink suggestion{(suggestionCount == 1 ? string.Empty : "s")}";
        RaiseRecoveryStateChanged();
        return false;
    }

    public async Task<bool> RelinkMissingMediaAsync(
        MissingMediaReferenceViewModel reference,
        string replacementPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        if (_pendingProjectDocument is null ||
            _pendingProjectPath is null ||
            !MissingMediaReferences.Contains(reference) ||
            _importMedia is null)
        {
            StatusText = "There is no pending media reference to relink.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(replacementPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText = $"The replacement path is invalid: {exception.Message}";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            StatusText = "The selected replacement file no longer exists.";
            return false;
        }

        var duplicatesAnotherSource = _pendingProjectDocument.Media.Any(media =>
            !PathComparer.Equals(media.SourcePath, reference.OriginalPath) &&
            PathComparer.Equals(
                _pendingProjectRelinks.GetValueOrDefault(media.SourcePath, media.SourcePath),
                fullPath));
        if (duplicatesAnotherSource)
        {
            StatusText = "That file is already used by another media item in this project.";
            return false;
        }

        using var candidate = new MediaItemViewModel(fullPath, reference.MediaId);
        await candidate.ProbeAsync(_importMedia, cancellationToken);
        if (!candidate.IsReady || candidate.Media is null)
        {
            StatusText = $"Could not use {candidate.DisplayName}: {candidate.Detail}";
            return false;
        }

        var mismatch = ValidateRelinkFingerprint(
            reference.SavedMedia,
            candidate,
            _pendingProjectDocument);
        if (mismatch is not null)
        {
            StatusText = $"{candidate.DisplayName} does not match the saved media: {mismatch}";
            return false;
        }

        reference.Resolve(fullPath);
        _pendingProjectRelinks[reference.OriginalPath] = fullPath;
        if (MissingMediaReferences.Any(item => !item.IsResolved))
        {
            StatusText = $"Relinked {reference.DisplayName}; choose the remaining media";
            return true;
        }

        var pendingPath = _pendingProjectPath;
        var isRecovery = _pendingProjectIsRecovery;
        var discardUnsavedChanges = _pendingProjectDiscardUnsavedChanges;
        var relinks = new Dictionary<string, string>(_pendingProjectRelinks, PathComparer);
        var opened = isRecovery
            ? await RecoverProjectAsync(pendingPath, cancellationToken, relinks)
            : await OpenProjectAsync(pendingPath, discardUnsavedChanges, cancellationToken, relinks);
        if (!opened)
        {
            return false;
        }

        ClearPendingProjectOpen();
        if (isRecovery)
        {
            var recoveredCandidate = RecoveryCandidates.FirstOrDefault(candidate =>
                PathComparer.Equals(candidate.RecoveryPath, pendingPath));
            if (recoveredCandidate is not null)
            {
                RecoveryCandidates.Remove(recoveredCandidate);
            }
        }
        else
        {
            IsProjectDirty = true;
            StatusText = "Opened with relinked media; save the project to keep the new locations";
            ScheduleAutosave();
        }

        RaiseRecoveryStateChanged();
        return true;
    }

    public void CancelPendingProjectOpen()
    {
        if (!HasPendingMissingMedia)
        {
            return;
        }

        ClearPendingProjectOpen();
        StatusText = "Canceled opening the project; the current workspace was not changed";
    }

    public async Task<bool> OpenProjectAsync(
        string projectPath,
        bool discardUnsavedChanges = false,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? mediaPathOverrides = null)
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
            if (mediaPathOverrides is { Count: > 0 })
            {
                document = document with
                {
                    Media = document.Media
                        .Select(media => mediaPathOverrides.TryGetValue(
                            media.SourcePath,
                            out var replacementPath)
                                ? media with { SourcePath = replacementPath }
                                : media)
                        .ToArray(),
                };
            }
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
            if (document.SchemaVersion >= 8 && document.ExportSettings is { } exportSettings)
            {
                if (document.SchemaVersion >= 9)
                {
                    ApplyCustomExportSettings(
                        exportSettings.CustomContainer,
                        exportSettings.CustomVideoCodec,
                        exportSettings.CustomAudioCodec,
                        exportSettings.CustomUseSourceFrameRate,
                        exportSettings.CustomFrameRate);
                }
            }
            if (!RememberExportAdjustments)
            {
                ResetTransientExportAdjustments();
            }

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
            if (document.SchemaVersion >= 7)
            {
                RebuildAudioTracksFromProject(document, warnings);
            }
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

                if (!TryRestoreMedia(mediaItem, savedMedia, document.SchemaVersion, out var warning))
                {
                    _unavailableProjectMedia.Add(savedMedia);
                    warnings.Add(warning!);
                }
            }

            RestoreVideoSequence(document, warnings);
            SelectedMedia ??= MediaItems.FirstOrDefault(item => item.IsReady);

            ProjectPath = Path.GetFullPath(projectPath);
            IsProjectDirty = false;
            ResetEditHistory();
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
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? mediaPathOverrides = null)
    {
        var recovered = await OpenProjectAsync(
            recoveryPath,
            discardUnsavedChanges: true,
            cancellationToken,
            mediaPathOverrides);
        if (!recovered)
        {
            return false;
        }

        ProjectPath = null;
        IsProjectDirty = true;
        SynchronizeEditHistoryBaseline();
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
            new ProjectCropSettingsDocument(SelectedCropAspectPreset.Id, IsCropAspectLocked),
            new ProjectCanvasDocument(
                CanvasSize.Width,
                CanvasSize.Height,
                CanvasCrop.X,
                CanvasCrop.Y,
                CanvasCrop.Width,
                CanvasCrop.Height),
            new ProjectExportSettingsDocument(
                ExportEncodingSettings.DefaultQuality,
                ExportEncodingSettings.DefaultScalePercent,
                ExportEncodingSettings.DefaultGifFrameRate,
                CustomExportContainer.Value,
                CustomVideoCodec.Value,
                CustomAudioCodec.Value,
                CustomUseSourceFrameRate,
                CustomFrameRate,
                ExportEncodingSettings.DefaultPlaybackSpeedPercent));
    }

    public void Dispose()
    {
        CancelAllKeyframeIndexing();
        DisposeMediaRuntimeValidation();
        DisposeExportHardwareCapabilityProbe();
        Updates.Dispose();
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
        OnPropertyChanged(nameof(ShowAdvancedClipControls));
        OnPropertyChanged(nameof(HasAudioTracks));
        OnPropertyChanged(nameof(CanRestoreMissingAudioTracks));
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
        OnPropertyChanged(nameof(CanRotateCanvas));
        OnPropertyChanged(nameof(CanMoveSelectedVideoLeft));
        OnPropertyChanged(nameof(CanMoveSelectedVideoRight));
        RaiseFastCutStateChanged();
        RaiseRecoveryStateChanged();
        RaiseSequenceStateChanged();
        RaiseExportStateChanged();
    }

    private void ClearProjectContent()
    {
        CancelAllKeyframeIndexing();
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
        ClearPendingProjectOpen();
        _timelineFrameCache.Clear();
        _isAdvancedMode = false;
        _moveTimelineClipsByDefault = false;
        _videoClipClipboard = null;
        _sequencePlayhead = MediaTime.Zero;
        _sequenceSelectionStart = MediaTime.Zero;
        _sequenceSelectionEnd = MediaTime.Zero;
        _sequenceTimelineZoom = 1;
        _sequenceTimelineViewportStart = 0;
        ResetProjectCanvasState();
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
            SynchronizeSequencePlayheadFromSource(sender as MediaItemViewModel);
            StartPreviewRefresh((MediaItemViewModel?)sender, debounce: true, clearExisting: false);
        }

        if (eventArgs.PropertyName is nameof(MediaItemViewModel.Crop) or nameof(MediaItemViewModel.Edit))
        {
            RaiseExportStateChanged();
            var mediaId = (sender as MediaItemViewModel)?.Id;
            MarkProjectDirty($"media:{mediaId}:{eventArgs.PropertyName}");
        }

        if (eventArgs.PropertyName is nameof(MediaItemViewModel.SelectionStart) or
            nameof(MediaItemViewModel.SelectionEnd))
        {
            RaiseExportStateChanged();
        }

    }

    private void SynchronizeSequencePlayheadFromSource(MediaItemViewModel? mediaItem)
    {
        if (mediaItem is null || SelectedVideoClip is not { } clip ||
            !ReferenceEquals(clip.Source, mediaItem) ||
            mediaItem.Playhead < clip.SourceStart || mediaItem.Playhead > clip.SourceEnd)
        {
            return;
        }

        var sourceOffset = Min(
            clip.Model.SourceRange.Duration,
            Max(MediaTime.Zero, mediaItem.Playhead - clip.SourceStart));
        var timelinePosition = clip.TimelineStart + clip.Model.SourceDurationToTimeline(sourceOffset);
        if (_sequencePlayhead == timelinePosition)
        {
            return;
        }

        _sequencePlayhead = timelinePosition;
        IsSequencePlayheadInGap = false;
        OnPropertyChanged(nameof(SequencePlayheadSeconds));
        OnPropertyChanged(nameof(SequencePlayheadText));
        OnPropertyChanged(nameof(CanSplitSelectedVideoClip));
        SynchronizeAudioTimelineState(refreshWaveforms: false);
    }

    private void RefreshAudioTimelineSegments(bool refreshWaveforms)
    {
        foreach (var track in AudioTracks)
        {
            var segments = CreateAudioTimelineSegments(track);
            track.SetTimelineSegments(segments);
        }

        SynchronizeAudioTimelineState(refreshWaveforms);
    }

    private void SynchronizeAudioGainTargets()
    {
        foreach (var track in AudioTracks)
        {
            var selectedClip = !track.IsExternal &&
                               SelectedVideoClip is { } clip &&
                               track.HasEmbeddedSource(clip.SourcePath)
                ? clip
                : null;
            track.SetContextualGainClip(selectedClip);
        }
    }

    private IReadOnlyList<AudioTimelineSegmentViewModel> CreateAudioTimelineSegments(
        AudioTrackViewModel track)
    {
        if (track.IsExternal)
        {
            return
            [
                new AudioTimelineSegmentViewModel(
                    null,
                    track.DisplayName,
                    track.TimelineOffset,
                    new MediaRange(MediaTime.Zero, track.Edit.SourceDuration),
                    track.SourcePath,
                    track.StreamIndex),
            ];
        }

        return VideoClips
            .Where(clip =>
                track.EmbeddedLaneIndex is { } laneIndex &&
                clip.IncludesAudioLane(laneIndex) &&
                track.HasEmbeddedSource(clip.SourcePath))
            .OrderBy(clip => clip.TimelineStart)
            .Select((clip, index) =>
            {
                track.TryGetEmbeddedStreamIndex(clip.SourcePath, out var streamIndex);
                return new AudioTimelineSegmentViewModel(
                    clip,
                    $"{index + 1}. {clip.DisplayName}",
                    clip.TimelineStart,
                    clip.Model.SourceRange,
                    clip.SourcePath,
                    streamIndex,
                    track.EmbeddedLaneIndex);
            })
            .ToArray();
    }

    private void SynchronizeAudioTimelineState(bool refreshWaveforms)
    {
        if (_isSynchronizingAudioTimeline)
        {
            return;
        }

        _isSynchronizingAudioTimeline = true;
        try
        {
            foreach (var track in AudioTracks)
            {
                var timelineDuration = SequenceDurationSeconds > 0
                    ? SequenceDurationSeconds
                    : track.TimelineSegments
                        .Select(segment => segment.TimelineEndSeconds)
                        .DefaultIfEmpty(track.DurationSeconds)
                        .Max();
                track.SynchronizeTimelineState(
                    timelineDuration,
                    SequencePlayheadSeconds,
                    SequenceSelectionStartSeconds,
                    SequenceSelectionEndSeconds,
                    SequenceTimelineZoom,
                    SequenceTimelineViewportStart,
                    IsSequenceTimelineFreeMode);
            }
        }
        finally
        {
            _isSynchronizingAudioTimeline = false;
        }

        if (refreshWaveforms && ShowAudioMixer)
        {
            foreach (var track in AudioTracks)
            {
                StartWaveformAnalysis(track, debounce: true);
            }
        }
    }

    private void OnAudioTrackPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender is not AudioTrackViewModel track)
        {
            return;
        }

        if (eventArgs.PropertyName is nameof(AudioTrackViewModel.Edit) or
            nameof(AudioTrackViewModel.GainDb) or
            nameof(AudioTrackViewModel.IsMuted) or
            nameof(AudioTrackViewModel.TimelineOffset))
        {
            MarkProjectDirty($"audio:{track.StableId}:{eventArgs.PropertyName}");
            RaiseExportStateChanged();
            OnPropertyChanged(nameof(PreviewAudioTracks));
        }

        if (eventArgs.PropertyName == nameof(AudioTrackViewModel.TimelineOffset))
        {
            RefreshAudioTimelineSegments(refreshWaveforms: true);
        }

        if (_isSynchronizingAudioTimeline)
        {
            return;
        }

        if (eventArgs.PropertyName is nameof(AudioTrackViewModel.TimelineZoom) or
            nameof(AudioTrackViewModel.TimelineViewportStart))
        {
            _sequenceTimelineZoom = track.TimelineZoom;
            _sequenceTimelineViewportStart = track.TimelineViewportStart;
            RaiseSequenceViewportChanged();
            StartSequenceTimelineAnalysis(debounce: true);
            return;
        }

        if (eventArgs.PropertyName == nameof(AudioTrackViewModel.TimelinePlayheadSeconds))
        {
            SequencePlayheadSeconds = track.TimelinePlayheadSeconds;
            return;
        }

        if (eventArgs.PropertyName == nameof(AudioTrackViewModel.TimelineSelectionStartSeconds))
        {
            SequenceSelectionStartSeconds = track.TimelineSelectionStartSeconds;
            return;
        }

        if (eventArgs.PropertyName == nameof(AudioTrackViewModel.TimelineSelectionEndSeconds))
        {
            SequenceSelectionEndSeconds = track.TimelineSelectionEndSeconds;
        }
    }

    private void AddAudioTracks(MediaItemViewModel mediaItem)
    {
        if (mediaItem.Media is null)
        {
            return;
        }

        var streams = mediaItem.Media.Probe.AudioStreams.ToArray();
        for (var laneIndex = 0; laneIndex < streams.Length; laneIndex++)
        {
            var stream = streams[laneIndex];
            if (mediaItem.IsExternalAudio)
            {
                if (AudioTracks.Any(track =>
                        track.IsExternal &&
                        PathComparer.Equals(track.SourcePath, mediaItem.SourcePath) &&
                        track.StreamIndex == stream.Index))
                {
                    continue;
                }
            }
            else if (AudioTracks.FirstOrDefault(track =>
                         !track.IsExternal && track.EmbeddedLaneIndex == laneIndex) is { } lane)
            {
                try
                {
                    lane.AddEmbeddedSource(mediaItem.Media, stream);
                }
                catch (ArgumentException)
                {
                    // Streams without usable duration remain visible in probe details only.
                }
                continue;
            }

            try
            {
                var track = new AudioTrackViewModel(
                    mediaItem.Media,
                    stream,
                    mediaItem.IsExternalAudio ? null : laneIndex);
                track.PropertyChanged += OnAudioTrackPropertyChanged;
                if (!track.IsExternal &&
                    SelectedVideoClip is { } selectedClip &&
                    track.HasEmbeddedSource(selectedClip.SourcePath))
                {
                    track.SetContextualGainClip(selectedClip);
                }
                AudioTracks.Add(track);
            }
            catch (ArgumentException)
            {
                // Streams without usable duration remain in probe details but cannot be edited yet.
            }
        }

        RaiseWorkspaceStateChanged();
        RefreshAudioTimelineSegments(refreshWaveforms: false);
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

        var fullRange = new MediaRange(MediaTime.Zero, duration.Value);
        var defaultTransform = CanvasSize == new PixelSize(1, 1)
            ? ClipCanvasTransform.Identity
            : ClipCanvasTransform.Fill(mediaItem.VideoSize, CanvasSize);
        AddVideoClipInstance(
            mediaItem,
            fullRange,
            fullRange,
            mediaItem.Crop,
            defaultTransform,
            audioGainDb: 0,
            NonNegativeTimelineTime(SequenceDurationSeconds),
            selectClip: VideoClips.Count == 0,
            collapseSelection: false);
    }

    private VideoClipViewModel AddVideoClipInstance(
        MediaItemViewModel mediaItem,
        MediaRange sourceRange,
        MediaRange availableRange,
        CropRegion sourceWindow,
        ClipCanvasTransform canvasTransform,
        double audioGainDb,
        MediaTime timelineStart,
        bool selectClip,
        bool collapseSelection,
        IEnumerable<int>? excludedAudioLaneIndices = null,
        int playbackSpeedPercent = SequenceClip.DefaultPlaybackSpeedPercent,
        IReadOnlyDictionary<int, double>? audioLaneGainDb = null)
    {
        var previousDuration = SequenceDurationSeconds;
        var selectionCoveredWholeSequence =
            _sequenceSelectionStart == MediaTime.Zero &&
            Math.Abs(_sequenceSelectionEnd.TotalSeconds - previousDuration) < 0.001;
        if (VideoClips.Count == 0 && CanvasSize == new PixelSize(1, 1))
        {
            InitializeCanvas(mediaItem.VideoSize, CropRegion.FullFrame(mediaItem.VideoSize));
            canvasTransform = ClipCanvasTransform.Fill(mediaItem.VideoSize, CanvasSize);
        }

        var model = new SequenceClip(
            Guid.NewGuid(),
            mediaItem.Id,
            sourceRange,
            availableRange,
            timelineStart,
            audioGainDb,
            playbackSpeedPercent);
        var clip = new VideoClipViewModel(
            mediaItem,
            model,
            sourceWindow,
            canvasTransform,
            excludedAudioLaneIndices,
            audioLaneGainDb);
        AttachVideoClip(clip);
        VideoClips.Add(clip);
        UpdateSequenceLayout(resetSelectionIfEmpty: false);

        if (selectionCoveredWholeSequence || VideoClips.Count == 1)
        {
            _sequenceSelectionStart = MediaTime.Zero;
            _sequenceSelectionEnd = SequenceTimeFromSeconds(SequenceDurationSeconds);
            RaiseSequenceSelectionChanged();
        }

        if (selectClip || SelectedVideoClip is null)
        {
            SelectedVideoClip = clip;
        }
        if (collapseSelection)
        {
            CollapseSequenceSelection(clip.TimelineStart);
        }
        StartSequenceTimelineAnalysis(debounce: false);
        return clip;
    }

    private MediaTime FindAvailableTimelineStart(MediaTime preferredStart, MediaTime duration)
    {
        var candidate = preferredStart < MediaTime.Zero ? MediaTime.Zero : preferredStart;
        foreach (var clip in VideoClips.OrderBy(static clip => clip.TimelineStart))
        {
            if (candidate + duration <= clip.TimelineStart)
            {
                return candidate;
            }

            if (candidate < clip.TimelineEnd && candidate + duration > clip.TimelineStart)
            {
                candidate = clip.TimelineEnd;
            }
        }

        return candidate;
    }

    private void AttachVideoClip(VideoClipViewModel clip)
    {
        clip.PropertyChanged += OnVideoClipPropertyChanged;
    }

    private void DetachVideoClip(VideoClipViewModel clip)
    {
        clip.PropertyChanged -= OnVideoClipPropertyChanged;
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
            RefreshAudioTimelineSegments(refreshWaveforms: false);
            StartSequenceTimelineAnalysis(debounce: true);
            MarkProjectDirty($"clip:{clip.Id}:model");
        }

        if (ReferenceEquals(clip, SelectedVideoClip) &&
            eventArgs.PropertyName is nameof(VideoClipViewModel.PlaybackSpeedPercent) or
                nameof(VideoClipViewModel.Model))
        {
            OnPropertyChanged(nameof(SelectedClipPlaybackSpeedPercent));
        }

        if (eventArgs.PropertyName is nameof(VideoClipViewModel.AudioGainDb) or
            nameof(VideoClipViewModel.AudioLaneGainDb))
        {
            MarkProjectDirty($"clip:{clip.Id}:audio-gain");
            RaiseExportStateChanged();
            OnPropertyChanged(nameof(PreviewAudioTracks));
        }

        if (eventArgs.PropertyName == nameof(VideoClipViewModel.CanvasTransform) &&
            _isClipTransformEditActive)
        {
            IsProjectDirty = true;
            _clipTransformChangedDuringEdit = true;
            return;
        }

        if (eventArgs.PropertyName is nameof(VideoClipViewModel.SourceWindow) or
            nameof(VideoClipViewModel.CanvasTransform))
        {
            MarkProjectDirty($"clip:{clip.Id}:visual-transform");
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
        var duration = VideoClips.Count == 0 ? MediaTime.Zero : VideoClips.Max(static clip => clip.TimelineEnd);
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
            _sequenceTimelineViewportStart,
            IsSequenceTimelineFreeMode);
        RaiseSequenceStateChanged();
        RefreshAudioTimelineSegments(refreshWaveforms: ShowAudioMixer);
    }


    private void RepackVideoClips()
    {
        var cursor = MediaTime.Zero;
        foreach (var clip in VideoClips)
        {
            clip.TimelineStart = cursor;
            cursor += clip.Duration;
        }
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
            IsSequencePlayheadInGap = VideoClips.Count > 0;
            return;
        }
        IsSequencePlayheadInGap = false;

        if (selectClip)
        {
            SelectedVideoClip = clip;
        }

        var offset = Min(clip.Duration, Max(MediaTime.Zero, timelineTime - clip.TimelineStart));
        clip.Source.Playhead = Min(
            clip.SourceEnd,
            clip.SourceStart + clip.Model.TimelineDurationToSource(offset));
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
        foreach (var clip in VideoClips.OrderBy(static clip => clip.TimelineStart))
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
                    clip.Model.TimelineTimeToSource(timelineStart),
                    clip.Model.TimelineTimeToSource(timelineEnd))));
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

    private static MediaTime NonNegativeTimelineTime(double seconds)
    {
        var bounded = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        return new MediaTime(
            checked((long)Math.Round(bounded * 1_000_000)),
            1_000_000);
    }

    private void RaiseSequenceSelectionChanged()
    {
        OnPropertyChanged(nameof(SequenceSelectionRangeText));
        OnPropertyChanged(nameof(SequenceSelectedDurationText));
        OnPropertyChanged(nameof(HasSequenceSelection));
        OnPropertyChanged(nameof(CanRemoveSequenceSelection));
        OnPropertyChanged(nameof(CanKeepSequenceSelection));
        SynchronizeAudioTimelineState(refreshWaveforms: false);
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
        OnPropertyChanged(nameof(IsSequenceTimelineFreeMode));
        OnPropertyChanged(nameof(SequenceTimelineModeText));
        SynchronizeAudioTimelineState(refreshWaveforms: true);
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
                            token,
                            IsHdrVideo(video));
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
                token,
                IsHdrVideo(clip.Source.Media?.Probe.VideoStreams.FirstOrDefault(
                    video => video.Index == videoStreamIndex)));
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

    private static string FormatTimelineViewportPosition(double seconds)
    {
        var finite = double.IsFinite(seconds) ? seconds : 0;
        var absolute = new MediaTime(
            checked((long)Math.Round(Math.Abs(finite) * 1_000)),
            1_000);
        return (finite < 0 ? "−" : string.Empty) + FormatSequenceTimestamp(absolute);
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
        OnPropertyChanged(nameof(ExportOutputSizeText));
        OnPropertyChanged(nameof(ExportSettingsSummary));
        OnPropertyChanged(nameof(SupportsHardwareVideoEncoding));
        OnPropertyChanged(nameof(ExportVideoEncoderStatus));
        OnPropertyChanged(nameof(IsPacketCopyExport));
        OnPropertyChanged(nameof(IsVideoStreamCopyExport));
        OnPropertyChanged(nameof(IsBoundaryGopExport));
        OnPropertyChanged(nameof(IsFullReencodeExport));
        OnPropertyChanged(nameof(ExportMethodTitle));
        OnPropertyChanged(nameof(ExportMethodDetails));
        OnPropertyChanged(nameof(CanApplyFastCopySettings));
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
            .Select(track => CreateAudioTrackDocumentForMedia(track, item))
            .OfType<ProjectAudioTrackDocument>()
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
        var transform = clip.CanvasTransform;
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
            window.Height,
            transform.OffsetX,
            transform.OffsetY,
            transform.Scale,
            transform.RotationDegrees,
            transform.ScaleX,
            transform.ScaleY,
            clip.TimelineStart.Numerator,
            clip.TimelineStart.Denominator,
            clip.AudioGainDb,
            clip.ExcludedAudioLaneIndices.Order().ToArray(),
            clip.PlaybackSpeedPercent,
            transform.IsHorizontallyMirrored,
            transform.IsVerticallyMirrored,
            clip.AudioLaneGainDb
                .OrderBy(pair => pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    private static ProjectAudioTrackDocument? CreateAudioTrackDocumentForMedia(
        AudioTrackViewModel track,
        MediaItemViewModel item)
    {
        SourceEdit edit;
        int streamIndex;
        int? laneIndex;
        IReadOnlyList<ProjectRangeDocument>? timelineSilencedRanges;
        MediaTime timelineOffset;
        if (track.IsExternal)
        {
            if (!PathComparer.Equals(track.SourcePath, item.SourcePath))
            {
                return null;
            }
            edit = track.Edit;
            streamIndex = track.StreamIndex;
            laneIndex = null;
            timelineSilencedRanges = null;
            timelineOffset = track.TimelineOffset;
        }
        else
        {
            if (track.EmbeddedLaneIndex is not { } embeddedLaneIndex ||
                !track.TryGetEmbeddedStreamIndex(item.SourcePath, out streamIndex))
            {
                return null;
            }
            edit = track.GetEmbeddedSourceEdit(item.SourcePath);
            laneIndex = embeddedLaneIndex;
            timelineSilencedRanges = track.TimelineSilencedRanges
                .Select(CreateRangeDocument)
                .ToArray();
            timelineOffset = MediaTime.Zero;
        }

        return new ProjectAudioTrackDocument(
            streamIndex,
            track.GainDb,
            track.IsMuted,
            edit.SourceDuration.Numerator,
            edit.SourceDuration.Denominator,
            edit.KeptRanges.Select(CreateRangeDocument).ToArray(),
            timelineOffset.Numerator,
            timelineOffset.Denominator,
            laneIndex,
            timelineSilencedRanges);
    }

    private void RestoreVideoSequence(ProjectDocument document, ICollection<string> warnings)
    {
        var replacements = new List<VideoClipViewModel>();
        if (document.SchemaVersion >= 2 && document.VideoClips is not null)
        {
            var mediaById = MediaItems.ToDictionary(item => item.Id);
            if (document.SchemaVersion >= 3 && document.Canvas is { } savedCanvas)
            {
                var canvasSize = new PixelSize(savedCanvas.Width, savedCanvas.Height);
                InitializeCanvas(
                    canvasSize,
                    new CropRegion(
                        canvasSize,
                        savedCanvas.CropX,
                        savedCanvas.CropY,
                        savedCanvas.CropWidth,
                        savedCanvas.CropHeight));
            }
            else
            {
                var firstSavedClip = document.VideoClips.FirstOrDefault(saved =>
                    mediaById.TryGetValue(saved.SourceMediaId, out var source) && source.HasVideo);
                if (firstSavedClip is not null &&
                    mediaById.TryGetValue(firstSavedClip.SourceMediaId, out var firstSource))
                {
                    var legacyCrop = new CropRegion(
                        firstSource.VideoSize,
                        firstSavedClip.SourceWindowX,
                        firstSavedClip.SourceWindowY,
                        firstSavedClip.SourceWindowWidth,
                        firstSavedClip.SourceWindowHeight);
                    InitializeCanvas(firstSource.VideoSize, legacyCrop);
                }
                else
                {
                    ResetProjectCanvasState();
                }
            }

            var legacyTimelineCursor = MediaTime.Zero;
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
                            new MediaTime(savedClip.AvailableEndNumerator, savedClip.AvailableEndDenominator)),
                        document.SchemaVersion >= 5
                            ? new MediaTime(savedClip.TimelineStartNumerator, savedClip.TimelineStartDenominator)
                            : legacyTimelineCursor,
                        document.SchemaVersion >= 6
                            ? savedClip.AudioGainDb
                            : 0,
                        document.SchemaVersion >= 10
                            ? savedClip.PlaybackSpeedPercent
                            : SequenceClip.DefaultPlaybackSpeedPercent);
                    var window = new CropRegion(
                        source.VideoSize,
                        savedClip.SourceWindowX,
                        savedClip.SourceWindowY,
                        savedClip.SourceWindowWidth,
                        savedClip.SourceWindowHeight);
                    var transform = document.SchemaVersion >= 3
                        ? new ClipCanvasTransform(
                            savedClip.CanvasOffsetX,
                            savedClip.CanvasOffsetY,
                            document.SchemaVersion >= 4
                                ? savedClip.CanvasScaleX ?? savedClip.CanvasScale
                                : savedClip.CanvasScale,
                            document.SchemaVersion >= 4
                                ? savedClip.CanvasScaleY ?? savedClip.CanvasScale
                                : savedClip.CanvasScale,
                            savedClip.CanvasRotationDegrees,
                            document.SchemaVersion >= 11 && savedClip.IsHorizontallyMirrored,
                            document.SchemaVersion >= 11 && savedClip.IsVerticallyMirrored)
                        : CreateLegacyCanvasTransform(source.VideoSize, CanvasSize, CanvasCrop, window);
                    replacements.Add(new VideoClipViewModel(
                        source,
                        model,
                        window,
                        transform,
                        document.SchemaVersion >= 7
                            ? savedClip.ExcludedAudioLaneIndices
                            : null,
                        document.SchemaVersion >= 12
                            ? savedClip.AudioLaneGainDb
                            : null));
                    if (document.SchemaVersion < 5)
                    {
                        legacyTimelineCursor += model.Duration;
                    }
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
            var firstSource = MediaItems.FirstOrDefault(item => item.HasVideo);
            if (firstSource is not null)
            {
                InitializeCanvas(firstSource.VideoSize, firstSource.Crop);
            }
            else
            {
                ResetProjectCanvasState();
            }

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
                        source.Crop,
                        CreateLegacyCanvasTransform(source.VideoSize, CanvasSize, CanvasCrop, source.Crop)));
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
        int schemaVersion,
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
                var audioEdit = SourceEdit.FromKeptRanges(
                    new MediaTime(
                        savedAudio.SourceDurationNumerator,
                        savedAudio.SourceDurationDenominator),
                    savedAudio.KeptRanges.Select(CreateMediaRange));
                if (item.IsExternalAudio)
                {
                    var track = AudioTracks.FirstOrDefault(candidate =>
                        candidate.IsExternal &&
                        PathComparer.Equals(candidate.SourcePath, item.SourcePath) &&
                        candidate.StreamIndex == savedAudio.StreamIndex) ??
                        throw new ArgumentException($"Audio stream {savedAudio.StreamIndex} is no longer available.");
                    track.Restore(
                        audioEdit,
                        savedAudio.GainDb,
                        savedAudio.IsMuted,
                        new MediaTime(
                            savedAudio.TimelineOffsetNumerator,
                            savedAudio.TimelineOffsetDenominator));
                }
                else
                {
                    var track = AudioTracks.FirstOrDefault(candidate =>
                        !candidate.IsExternal &&
                        candidate.HasEmbeddedSource(item.SourcePath, savedAudio.StreamIndex) &&
                        (schemaVersion < 7 || candidate.EmbeddedLaneIndex == savedAudio.LaneIndex)) ??
                        throw new ArgumentException($"Audio stream {savedAudio.StreamIndex} is no longer available.");
                    track.RestoreEmbeddedSource(
                        item.SourcePath,
                        savedAudio.StreamIndex,
                        audioEdit,
                        savedAudio.GainDb,
                        savedAudio.IsMuted,
                        schemaVersion >= 7
                            ? (savedAudio.TimelineSilencedRanges ?? []).Select(CreateMediaRange)
                            : null);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            warning = $"{item.DisplayName} could not restore its saved edits: {exception.Message}";
            return false;
        }
    }

    private void RebuildAudioTracksFromProject(ProjectDocument document, ICollection<string> warnings)
    {
        foreach (var existing in AudioTracks.ToArray())
        {
            RemoveAudioTrackCore(existing);
        }

        var mediaById = MediaItems.ToDictionary(item => item.Id);
        foreach (var savedMedia in document.Media)
        {
            if (!mediaById.TryGetValue(savedMedia.MediaId, out var item) || item.Media is null)
            {
                continue;
            }

            foreach (var savedAudio in savedMedia.AudioTracks ?? [])
            {
                var stream = item.Media.Probe.AudioStreams.FirstOrDefault(candidate =>
                    candidate.Index == savedAudio.StreamIndex);
                if (stream is null)
                {
                    warnings.Add($"{item.DisplayName} audio stream {savedAudio.StreamIndex} is unavailable");
                    continue;
                }

                try
                {
                    if (item.IsExternalAudio)
                    {
                        var external = new AudioTrackViewModel(item.Media, stream);
                        external.PropertyChanged += OnAudioTrackPropertyChanged;
                        AudioTracks.Add(external);
                        continue;
                    }

                    if (savedAudio.LaneIndex is not { } laneIndex)
                    {
                        warnings.Add($"{item.DisplayName} has an embedded audio binding without a lane");
                        continue;
                    }

                    var lane = AudioTracks.FirstOrDefault(candidate =>
                        !candidate.IsExternal && candidate.EmbeddedLaneIndex == laneIndex);
                    if (lane is null)
                    {
                        lane = new AudioTrackViewModel(item.Media, stream, laneIndex);
                        lane.PropertyChanged += OnAudioTrackPropertyChanged;
                        AudioTracks.Add(lane);
                    }
                    else
                    {
                        lane.AddEmbeddedSource(item.Media, stream);
                    }
                }
                catch (ArgumentException exception)
                {
                    warnings.Add($"{item.DisplayName} audio could not be restored: {exception.Message}");
                }
            }
        }

        RaiseWorkspaceStateChanged();
        RefreshAudioTimelineSegments(refreshWaveforms: false);
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

    private void MarkProjectDirty(string? historyMergeKey = null)
    {
        if (_isLoadingProject || _isApplyingEditHistory)
        {
            return;
        }

        IsProjectDirty = true;
        RecordEditHistory(historyMergeKey);
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
            var pruneResult = await PruneRecoveryFilesAsync(request.Token);
            if (pruneResult.FailedFiles > 0)
            {
                StatusText = $"Autosave warning: {pruneResult.FailedFiles} stale recovery file{(pruneResult.FailedFiles == 1 ? string.Empty : "s")} could not be cleaned up";
            }
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

    private Task<RecoveryPruneResult> PruneRecoveryFilesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_recoveryDirectory))
        {
            return Task.FromResult(default(RecoveryPruneResult));
        }

        var recoveryDirectory = _recoveryDirectory;
        var retentionDays = RecoveryRetentionDays;
        var maximumRecoveryFiles = MaximumRecoveryFiles;
        return Task.Run(
            () => RecoveryRetentionPruner.Prune(
                recoveryDirectory,
                retentionDays,
                maximumRecoveryFiles,
                DateTimeOffset.UtcNow,
                cancellationToken),
            cancellationToken);
    }

    private static string? GetUnavailableMediaReason(ProjectMediaDocument media)
    {
        try
        {
            var fullPath = Path.GetFullPath(media.SourcePath);
            if (!File.Exists(fullPath))
            {
                return "File not found";
            }

            if (media.ExpectedFileSizeBytes is { } expectedBytes)
            {
                var actualBytes = new FileInfo(fullPath).Length;
                if (actualBytes != expectedBytes)
                {
                    return $"File size changed (saved {expectedBytes:N0} bytes, current {actualBytes:N0} bytes)";
                }
            }

            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException)
        {
            return $"File cannot be accessed: {exception.Message}";
        }
    }

    private static string? ValidateRelinkFingerprint(
        ProjectMediaDocument saved,
        MediaItemViewModel candidate,
        ProjectDocument pendingProject)
    {
        var probe = candidate.Media!.Probe;
        if (saved.ExpectedFileSizeBytes is { } expectedBytes &&
            probe.FileSizeBytes is { } actualBytes &&
            expectedBytes != actualBytes)
        {
            return $"file size is {actualBytes:N0} bytes, expected {expectedBytes:N0}";
        }

        var requiresVideo = pendingProject.VideoClips?.Any(clip =>
            clip.SourceMediaId == saved.MediaId) == true;
        if (requiresVideo)
        {
            if (!candidate.HasVideo)
            {
                return "the saved item is video but the replacement has no video stream";
            }

            var expectedSize = new PixelSize(saved.SourceWidth, saved.SourceHeight);
            if (candidate.VideoSize != expectedSize)
            {
                return $"video size is {candidate.VideoSize.Width} × {candidate.VideoSize.Height}, expected {expectedSize.Width} × {expectedSize.Height}";
            }
        }

        var expectedDuration = new MediaTime(
            saved.SourceDurationNumerator,
            saved.SourceDurationDenominator);
        if (probe.Duration is not { } actualDuration ||
            Math.Abs(actualDuration.TotalSeconds - expectedDuration.TotalSeconds) > 0.1)
        {
            return $"duration differs from the saved {expectedDuration.TotalSeconds:0.###} seconds";
        }

        var availableAudioStreams = probe.AudioStreams
            .Select(stream => stream.Index)
            .ToHashSet();
        var missingStream = (saved.AudioTracks ?? [])
            .Select(track => track.StreamIndex)
            .FirstOrDefault(index => !availableAudioStreams.Contains(index), -1);
        if (missingStream >= 0)
        {
            return $"audio stream {missingStream} is missing";
        }

        return null;
    }

    private void ClearPendingProjectOpen()
    {
        _pendingProjectDocument = null;
        _pendingProjectPath = null;
        _pendingProjectIsRecovery = false;
        _pendingProjectDiscardUnsavedChanges = false;
        _pendingProjectRelinks.Clear();
        MissingMediaReferences.Clear();
        RaiseRecoveryStateChanged();
    }

    private void RaiseRecoveryStateChanged()
    {
        OnPropertyChanged(nameof(HasRecoveryCandidates));
        OnPropertyChanged(nameof(HasPendingMissingMedia));
        OnPropertyChanged(nameof(PendingProjectOpenTitle));
        OnPropertyChanged(nameof(PendingProjectOpenDescription));
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
        if (!clearExisting)
        {
            ShowNearestCachedPreview(mediaItem);
        }
        _ = RefreshPreviewAsync(mediaItem, request, debounce, clearExisting);
    }

    private void ShowNearestCachedPreview(MediaItemViewModel? mediaItem)
    {
        if (mediaItem?.Media?.Probe.VideoStreams.FirstOrDefault() is not { } video)
        {
            return;
        }

        var sourceSeconds = mediaItem.Playhead.TotalSeconds;
        var maximumDistance = Math.Max(
            1,
            mediaItem.TimelineViewportDurationSeconds / SequenceViewportThumbnailCount);
        if (!_timelineFrameCache.TryGetNearest(
                mediaItem.SourcePath,
                video.Index,
                sourceSeconds,
                maximumDistance,
                out var key,
                out var encodedImage) ||
            _previewCacheKey == key)
        {
            return;
        }

        using var stream = new MemoryStream(encodedImage, writable: false);
        PreviewImage = new Bitmap(stream);
        _previewCacheKey = key;
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
            _previewCacheKey = null;
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
            var previewTimestamp = ToMediaTime(Math.Min(
                mediaItem!.Playhead.TotalSeconds,
                Math.Max(0, mediaItem.SourceDurationSeconds - mediaItem.FrameStepSeconds)));
            var decodedFrame = await _frameDecoder.DecodeAsync(
                mediaItem.SourcePath,
                video.Index,
                previewTimestamp,
                new PixelSize(1_280, 720),
                cancellationToken,
                IsHdrVideo(video));

            await using var stream = new MemoryStream(decodedFrame.EncodedImage.ToArray(), writable: false);
            cancellationToken.ThrowIfCancellationRequested();
            PreviewImage = new Bitmap(stream);
            _previewCacheKey = null;
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
                        cancellationToken,
                        IsHdrVideo(video));
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
        const int maximumRenderedSegments = 48;
        var cancellationToken = request.Token;
        var visuals = new List<TimelineBitmapVisual>();
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken);
            }

            var viewportStart = track.TimelineViewportStart;
            var viewportEnd = track.TimelineViewportEndSeconds;
            if (viewportEnd <= viewportStart)
            {
                return;
            }

            var visibleSegments = track.TimelineSegments
                .Select(segment => (
                    Segment: segment,
                    Start: Math.Max(segment.TimelineStartSeconds, viewportStart),
                    End: Math.Min(segment.TimelineEndSeconds, viewportEnd)))
                .Where(item => item.End > item.Start)
                .ToList();
            track.IsWaveformDecimated = visibleSegments.Count > maximumRenderedSegments;
            if (visibleSegments.Count > maximumRenderedSegments)
            {
                var original = visibleSegments;
                visibleSegments = Enumerable.Range(0, maximumRenderedSegments)
                    .Select(index => original[(int)((long)index * original.Count / maximumRenderedSegments)])
                    .Distinct()
                    .ToList();
            }

            track.IsWaveformLoading = true;
            track.WaveformErrorText = null;
            var renderTasks = visibleSegments.Select(async visible =>
            {
                var sourceStart = visible.Segment.TimelineTimeToSource(
                    ToMediaTime(visible.Start)).TotalSeconds;
                var sourceEnd = visible.Segment.TimelineTimeToSource(
                    ToMediaTime(visible.End)).TotalSeconds;
                var pixelWidth = Math.Clamp(
                    (int)Math.Ceiling(
                        1_600 * (visible.End - visible.Start) /
                        Math.Max(0.000001, viewportEnd - viewportStart)),
                    32,
                    1_600);
                await _analysisSlots.WaitAsync(cancellationToken);
                WaveformImage image;
                try
                {
                    image = await _waveformRenderer!.RenderAsync(
                        visible.Segment.SourcePath ?? track.SourcePath,
                        visible.Segment.StreamIndex >= 0
                            ? visible.Segment.StreamIndex
                            : track.StreamIndex,
                        new MediaRange(ToMediaTime(sourceStart), ToMediaTime(sourceEnd)),
                        new PixelSize(pixelWidth, 72),
                        cancellationToken);
                }
                finally
                {
                    _analysisSlots.Release();
                }

                await using var stream = new MemoryStream(image.EncodedImage.ToArray(), writable: false);
                cancellationToken.ThrowIfCancellationRequested();
                var visual = new TimelineBitmapVisual(visible.Start, visible.End, new Bitmap(stream));
                lock (visuals)
                {
                    visuals.Add(visual);
                }
            });
            await Task.WhenAll(renderTasks);

            if (_waveformCancellations.TryGetValue(track, out var activeRequest) &&
                ReferenceEquals(activeRequest, request))
            {
                track.SetWaveform(null);
                track.SetWaveforms(visuals.OrderBy(visual => visual.Start).ToArray());
                visuals.Clear();
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
            foreach (var visual in visuals)
            {
                visual.Dispose();
            }

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

    private static bool IsHdrVideo(VideoStreamInfo? video) =>
        video?.ColorTransfer is not null &&
        (video.ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
         video.ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase));

    private static double CombineAudioGain(double trackGainDb, double clipGainDb) =>
        Math.Clamp(trackGainDb + clipGainDb, -60, 12);

    [Flags]
    private enum PacketCopyBlocker
    {
        None = 0,
        Quality = 1 << 0,
        ExportScale = 1 << 1,
        ExportSpeed = 1 << 2,
        Format = 1 << 3,
        ClipCount = 1 << 4,
        Media = 1 << 5,
        SourceRange = 1 << 6,
        TimelineRange = 1 << 7,
        ClipSpeed = 1 << 8,
        SourceRotation = 1 << 9,
        Transform = 1 << 10,
        Canvas = 1 << 11,
        Crop = 1 << 12,
        VideoCodec = 1 << 13,
        FrameRate = 1 << 14,
        ExternalAudio = 1 << 15,
        AudioLayout = 1 << 16,
        AudioGain = 1 << 17,
        AudioEdit = 1 << 18,
        AudioCodec = 1 << 19,
        SequenceCompatibility = 1 << 20,
    }

    private readonly record struct PacketCopyDecision(
        ExportStrategy Strategy,
        PacketCopyBlocker Blockers,
        ImmutableArray<string> Reasons)
    {
        public static PacketCopyDecision Copy { get; } = new(
            ExportStrategy.StreamCopy,
            PacketCopyBlocker.None,
            []);

        public static PacketCopyDecision CopyConcat { get; } = new(
            ExportStrategy.ConcatStreamCopy,
            PacketCopyBlocker.None,
            []);

        public static PacketCopyDecision CopyEditListTrim(IEnumerable<string> reasons) => new(
            ExportStrategy.EditListStreamCopy,
            PacketCopyBlocker.None,
            reasons.ToImmutableArray());

        public static PacketCopyDecision CopyVideo(
            PacketCopyBlocker blockers,
            IEnumerable<string> reasons) => new(
                ExportStrategy.VideoStreamCopy,
                blockers,
                reasons.ToImmutableArray());

        public static PacketCopyDecision BoundaryGop(
            PacketCopyBlocker blockers,
            IEnumerable<string> reasons) => new(
                ExportStrategy.BoundaryGop,
                blockers,
                reasons.ToImmutableArray());

        public static PacketCopyDecision Transcode(
            PacketCopyBlocker blockers,
            IEnumerable<string> reasons) => new(
                ExportStrategy.ExactTranscode,
                blockers,
                reasons.ToImmutableArray());
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

internal readonly record struct VideoClipClipboard(
    Guid SourceId,
    MediaRange SourceRange,
    MediaRange AvailableRange,
    CropRegion SourceWindow,
    ClipCanvasTransform CanvasTransform,
    double AudioGainDb,
    int PlaybackSpeedPercent,
    ImmutableArray<int> ExcludedAudioLaneIndices,
    ImmutableDictionary<int, double> AudioLaneGainDb);
