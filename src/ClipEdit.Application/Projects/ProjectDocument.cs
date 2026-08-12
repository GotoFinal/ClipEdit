namespace ClipEdit.Application.Projects;

public sealed record ProjectDocument(
    int SchemaVersion,
    Guid ProjectId,
    string? ExportPresetId,
    IReadOnlyList<ProjectMediaDocument> Media)
{
    public const int CurrentSchemaVersion = 1;
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
    IReadOnlyList<ProjectAudioTrackDocument>? AudioTracks = null);

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
    IReadOnlyList<ProjectRangeDocument> KeptRanges);
