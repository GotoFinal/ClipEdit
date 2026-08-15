using ClipEdit.Application.Projects;
using ClipEdit.Application.Export;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaximumEditHistoryEntries = 100;
    private static readonly TimeSpan ContinuousEditMergeWindow = TimeSpan.FromMilliseconds(700);
    private readonly List<EditHistoryState> _undoHistory = [];
    private readonly List<EditHistoryState> _redoHistory = [];
    private EditHistoryState? _currentEditHistoryState;
    private string? _lastHistoryMergeKey;
    private DateTimeOffset _lastHistoryEditAt;
    private bool _isApplyingEditHistory;

    public bool CanUndo => _undoHistory.Count > 0 && !IsBusy && !IsExporting;

    public bool CanRedo => _redoHistory.Count > 0 && !IsBusy && !IsExporting;

    public bool Undo()
    {
        if (!CanUndo || _currentEditHistoryState is null)
        {
            return false;
        }

        var target = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(_currentEditHistoryState);
        ApplyEditHistoryState(target);
        StatusText = "Undid the last project edit";
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo || _currentEditHistoryState is null)
        {
            return false;
        }

        var target = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(_currentEditHistoryState);
        ApplyEditHistoryState(target);
        StatusText = "Redid the last project edit";
        return true;
    }

    private void RecordEditHistory(string? mergeKey)
    {
        var next = CaptureEditHistoryState();
        if (_currentEditHistoryState is null ||
            !UsesSameMediaSet(_currentEditHistoryState.Document, next.Document))
        {
            ResetEditHistory(next);
            return;
        }

        if (ProjectDocumentsEquivalent(_currentEditHistoryState.Document, next.Document))
        {
            _currentEditHistoryState = next;
            RaiseEditHistoryChanged();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var mergeWithPrevious = mergeKey is not null &&
                                mergeKey == _lastHistoryMergeKey &&
                                now - _lastHistoryEditAt <= ContinuousEditMergeWindow;
        if (!mergeWithPrevious)
        {
            _undoHistory.Add(_currentEditHistoryState);
            if (_undoHistory.Count > MaximumEditHistoryEntries)
            {
                _undoHistory.RemoveAt(0);
            }
        }

        _redoHistory.Clear();
        _currentEditHistoryState = next;
        _lastHistoryMergeKey = mergeKey;
        _lastHistoryEditAt = now;
        RaiseEditHistoryChanged();
    }

    private void ResetEditHistory()
    {
        ResetEditHistory(CaptureEditHistoryState());
    }

    private void ResetEditHistory(EditHistoryState current)
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _currentEditHistoryState = current;
        _lastHistoryMergeKey = null;
        _lastHistoryEditAt = default;
        RaiseEditHistoryChanged();
    }

    private void SynchronizeEditHistoryBaseline()
    {
        _currentEditHistoryState = CaptureEditHistoryState();
        _lastHistoryMergeKey = null;
        _lastHistoryEditAt = default;
        RaiseEditHistoryChanged();
    }

    private EditHistoryState CaptureEditHistoryState() => new(
        CreateProjectDocument(),
        SelectedMedia?.Id,
        SelectedVideoClip?.Id,
        _sequencePlayhead,
        _sequenceSelectionStart,
        _sequenceSelectionEnd,
        IsProjectDirty);

    private void ApplyEditHistoryState(EditHistoryState state)
    {
        var previousLoadingState = _isLoadingProject;
        _isApplyingEditHistory = true;
        _isLoadingProject = true;
        try
        {
            var document = state.Document;
            _projectId = document.ProjectId;
            SelectedExportPreset = ExportPresets.FirstOrDefault(
                                       preset => preset.Id == document.ExportPresetId) ??
                                   BuiltInExportPresets.Mp4Compatible;
            if (document.ExportSettings is { } exportSettings)
            {
                ApplyCustomExportSettings(
                    exportSettings.CustomContainer,
                    exportSettings.CustomVideoCodec,
                    exportSettings.CustomAudioCodec,
                    exportSettings.CustomUseSourceFrameRate,
                    exportSettings.CustomFrameRate);
            }

            var warnings = new List<string>();
            if (!AudioTopologyEquivalent(_currentEditHistoryState?.Document, document))
            {
                RebuildAudioTracksFromProject(document, warnings);
            }

            for (var destinationIndex = 0; destinationIndex < document.Media.Count; destinationIndex++)
            {
                var mediaId = document.Media[destinationIndex].MediaId;
                var sourceIndex = MediaItems
                    .Select((item, index) => (item, index))
                    .First(entry => entry.item.Id == mediaId)
                    .index;
                if (sourceIndex != destinationIndex)
                {
                    MediaItems.Move(sourceIndex, destinationIndex);
                }
            }

            foreach (var savedMedia in document.Media)
            {
                var mediaItem = MediaItems.FirstOrDefault(item => item.Id == savedMedia.MediaId);
                if (mediaItem is not null)
                {
                    TryRestoreMedia(mediaItem, savedMedia, document.SchemaVersion, out _);
                }
            }

            RestoreVideoSequence(document, warnings);
            SelectedMedia = state.SelectedMediaId is { } selectedMediaId
                ? MediaItems.FirstOrDefault(item => item.Id == selectedMediaId)
                : MediaItems.FirstOrDefault();
            SelectedVideoClip = state.SelectedVideoClipId is { } selectedClipId
                ? VideoClips.FirstOrDefault(clip => clip.Id == selectedClipId)
                : VideoClips.FirstOrDefault();
            _sequencePlayhead = state.SequencePlayhead;
            _sequenceSelectionStart = state.SequenceSelectionStart;
            _sequenceSelectionEnd = state.SequenceSelectionEnd;
            IsProjectDirty = state.IsProjectDirty;
            RaiseWorkspaceStateChanged();
            SyncSourcePreviewToSequenceTime(_sequencePlayhead, selectClip: false);
            if (IsProjectDirty)
            {
                ScheduleAutosave();
            }
            else
            {
                _autosaveCancellation?.Cancel();
            }

            _currentEditHistoryState = state;
            _lastHistoryMergeKey = null;
            _lastHistoryEditAt = default;
        }
        finally
        {
            _isLoadingProject = previousLoadingState;
            _isApplyingEditHistory = false;
            RaiseEditHistoryChanged();
        }
    }

    private void RaiseEditHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private static bool UsesSameMediaSet(ProjectDocument left, ProjectDocument right)
    {
        if (left.Media.Count != right.Media.Count)
        {
            return false;
        }

        var rightIds = right.Media.Select(media => media.MediaId).ToHashSet();
        return left.Media.All(media => rightIds.Contains(media.MediaId));
    }

    private static bool ProjectDocumentsEquivalent(ProjectDocument left, ProjectDocument right)
    {
        return left.SchemaVersion == right.SchemaVersion &&
               left.ProjectId == right.ProjectId &&
               left.ExportPresetId == right.ExportPresetId &&
               left.CropSettings == right.CropSettings &&
               left.Canvas == right.Canvas &&
               left.ExportSettings == right.ExportSettings &&
               left.Media.Count == right.Media.Count &&
               left.Media.Zip(right.Media).All(pair => MediaDocumentsEquivalent(pair.First, pair.Second)) &&
               (left.VideoClips ?? []).Count == (right.VideoClips ?? []).Count &&
               (left.VideoClips ?? []).Zip(right.VideoClips ?? [])
                   .All(pair => VideoClipDocumentsEquivalent(pair.First, pair.Second));
    }

    private static bool MediaDocumentsEquivalent(ProjectMediaDocument left, ProjectMediaDocument right)
    {
        return left.SourcePath == right.SourcePath &&
               left.ExpectedFileSizeBytes == right.ExpectedFileSizeBytes &&
               left.SourceWidth == right.SourceWidth &&
               left.SourceHeight == right.SourceHeight &&
               left.CropX == right.CropX &&
               left.CropY == right.CropY &&
               left.CropWidth == right.CropWidth &&
               left.CropHeight == right.CropHeight &&
               left.SourceDurationNumerator == right.SourceDurationNumerator &&
               left.SourceDurationDenominator == right.SourceDurationDenominator &&
               left.MediaId == right.MediaId &&
               left.KeptRanges.SequenceEqual(right.KeptRanges) &&
               AudioDocumentsEquivalent(left.AudioTracks ?? [], right.AudioTracks ?? []);
    }

    private static bool AudioDocumentsEquivalent(
        IReadOnlyList<ProjectAudioTrackDocument> left,
        IReadOnlyList<ProjectAudioTrackDocument> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.StreamIndex == pair.Second.StreamIndex &&
            pair.First.GainDb.Equals(pair.Second.GainDb) &&
            pair.First.IsMuted == pair.Second.IsMuted &&
            pair.First.SourceDurationNumerator == pair.Second.SourceDurationNumerator &&
            pair.First.SourceDurationDenominator == pair.Second.SourceDurationDenominator &&
            pair.First.TimelineOffsetNumerator == pair.Second.TimelineOffsetNumerator &&
            pair.First.TimelineOffsetDenominator == pair.Second.TimelineOffsetDenominator &&
            pair.First.LaneIndex == pair.Second.LaneIndex &&
            pair.First.KeptRanges.SequenceEqual(pair.Second.KeptRanges) &&
            (pair.First.TimelineSilencedRanges ?? [])
                .SequenceEqual(pair.Second.TimelineSilencedRanges ?? []));
    }

    private static bool VideoClipDocumentsEquivalent(
        ProjectVideoClipDocument left,
        ProjectVideoClipDocument right)
    {
        return left with { ExcludedAudioLaneIndices = null } ==
               right with { ExcludedAudioLaneIndices = null } &&
               (left.ExcludedAudioLaneIndices ?? [])
               .SequenceEqual(right.ExcludedAudioLaneIndices ?? []);
    }

    private static bool AudioTopologyEquivalent(ProjectDocument? left, ProjectDocument right)
    {
        if (left is null)
        {
            return false;
        }

        var leftBindings = CreateAudioTopology(left);
        var rightBindings = CreateAudioTopology(right);
        return leftBindings.SetEquals(rightBindings);
    }

    private static HashSet<(Guid MediaId, int StreamIndex, int? LaneIndex)> CreateAudioTopology(
        ProjectDocument document)
    {
        return document.Media
            .SelectMany(media => (media.AudioTracks ?? []).Select(audio =>
                (media.MediaId, audio.StreamIndex, audio.LaneIndex)))
            .ToHashSet();
    }

    private sealed record EditHistoryState(
        ProjectDocument Document,
        Guid? SelectedMediaId,
        Guid? SelectedVideoClipId,
        MediaTime SequencePlayhead,
        MediaTime SequenceSelectionStart,
        MediaTime SequenceSelectionEnd,
        bool IsProjectDirty);
}
