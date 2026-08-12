namespace ClipEdit.Application.Projects;

public sealed record ProjectDocument(
    int SchemaVersion,
    Guid ProjectId,
    string? ExportPresetId,
    IReadOnlyList<ProjectMediaDocument> Media,
    IReadOnlyList<ProjectVideoClipDocument>? VideoClips = null,
    ProjectCropSettingsDocument? CropSettings = null)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record ProjectMediaDocument(
    string SourcePath,
    long? ExpectedFileSizeBytes,
    int SourceWidth,
    int SourceHeight,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    long SourceDurationNumerator,
    int SourceDurationDenominator,
    IReadOnlyList<ProjectRangeDocument> KeptRanges,
    IReadOnlyList<ProjectAudioTrackDocument>? AudioTracks = null,
    Guid MediaId = default);

public sealed record ProjectVideoClipDocument(
    Guid ClipId,
    Guid SourceMediaId,
    long SourceStartNumerator,
    int SourceStartDenominator,
    long SourceEndNumerator,
    int SourceEndDenominator,
    long AvailableStartNumerator,
    int AvailableStartDenominator,
    long AvailableEndNumerator,
    int AvailableEndDenominator,
    int SourceWindowX,
    int SourceWindowY,
    int SourceWindowWidth,
    int SourceWindowHeight);

public sealed record ProjectCropSettingsDocument(
    string PresetId,
    bool IsAspectLocked);

public sealed record ProjectRangeDocument(
    long StartNumerator,
    int StartDenominator,
    long EndNumerator,
    int EndDenominator);

public sealed record ProjectAudioTrackDocument(
    int StreamIndex,
    double GainDb,
    bool IsMuted,
    long SourceDurationNumerator,
    int SourceDurationDenominator,
    IReadOnlyList<ProjectRangeDocument> KeptRanges,
    long TimelineOffsetNumerator = 0,
    int TimelineOffsetDenominator = 1);
