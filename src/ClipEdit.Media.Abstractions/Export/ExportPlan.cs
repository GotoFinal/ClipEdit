using System.Collections.Immutable;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Export;

public enum ExportStrategy
{
    ExactTranscode,
}

/// <summary>
/// A validated, immutable rendering decision for one source video.
/// </summary>
public sealed record ExportPlan
{
    private readonly PixelSize _outputSize;

    public ExportPlan(
        string sourcePath,
        string destinationPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        CropRegion crop,
        ImmutableArray<MediaRange> sourceRanges,
        ExportPreset preset,
        bool replaceExistingDestination = false,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        ArgumentNullException.ThrowIfNull(preset);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The export source path must be absolute.", nameof(sourcePath));
        }

        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination path must be absolute.", nameof(destinationPath));
        }

        if (sourceRanges.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one non-empty source range is required.", nameof(sourceRanges));
        }

        ValidateRanges(sourceRanges);
        if (preset.RequiresEvenDimensions &&
            ((crop.Width & 1) != 0 || (crop.Height & 1) != 0))
        {
            throw new ExportPlanException(
                $"{preset.DisplayName} requires even dimensions, but the crop is {crop.Width} × {crop.Height}.");
        }

        SourcePath = Path.GetFullPath(sourcePath);
        DestinationPath = Path.GetFullPath(destinationPath);
        VideoStreamIndex = videoStreamIndex;
        AudioTracks = audioTracks.IsDefault
            ? audioStreamIndex is null
                ? []
                : [new ExportAudioTrackPlan(audioStreamIndex.Value, 0)]
            : audioTracks;
        if (AudioTracks.Any(track => track is null) || HasDuplicateAudioTracks(AudioTracks))
        {
            throw new ArgumentException(
                "Export audio tracks must be non-null and use distinct source/stream identities.",
                nameof(audioTracks));
        }
        Crop = crop;
        SourceRanges = sourceRanges;
        Preset = preset;
        ReplaceExistingDestination = replaceExistingDestination;
        VideoSegments = [];
        _outputSize = crop.ExportSize;
        SequenceTimelineStart = MediaTime.Zero;
    }

    public ExportPlan(
        ImmutableArray<ExportVideoSegmentPlan> videoSegments,
        PixelSize outputSize,
        string destinationPath,
        ExportPreset preset,
        bool replaceExistingDestination = false,
        ImmutableArray<ExportAudioTrackPlan> externalAudioTracks = default,
        MediaTime sequenceTimelineStart = default)
    {
        if (videoSegments.IsDefaultOrEmpty || videoSegments.Any(segment => segment is null))
        {
            throw new ArgumentException("At least one video segment is required.", nameof(videoSegments));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(preset);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination path must be absolute.", nameof(destinationPath));
        }

        if (preset.RequiresEvenDimensions &&
            (((outputSize.Width & 1) != 0) || ((outputSize.Height & 1) != 0)))
        {
            throw new ExportPlanException(
                $"{preset.DisplayName} requires even dimensions, but the output is {outputSize.Width} × {outputSize.Height}.");
        }

        var externalTracks = externalAudioTracks.IsDefault ? [] : externalAudioTracks;
        if (sequenceTimelineStart < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceTimelineStart));
        }
        if (externalTracks.Any(track => track is null || !track.IsExternal) ||
            HasDuplicateAudioTracks(externalTracks))
        {
            throw new ArgumentException(
                "Sequence-level audio tracks must be distinct external sources.",
                nameof(externalAudioTracks));
        }

        VideoSegments = videoSegments;
        _outputSize = outputSize;
        DestinationPath = Path.GetFullPath(destinationPath);
        Preset = preset;
        ReplaceExistingDestination = replaceExistingDestination;
        AudioTracks = externalTracks;
        SourcePath = videoSegments[0].SourcePath;
        VideoStreamIndex = videoSegments[0].VideoStreamIndex;
        Crop = videoSegments[0].Crop;
        SourceRanges = videoSegments.Select(segment => segment.SourceRange).ToImmutableArray();
        SequenceTimelineStart = sequenceTimelineStart;
    }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public int VideoStreamIndex { get; }

    public ImmutableArray<ExportAudioTrackPlan> AudioTracks { get; }

    public ImmutableArray<ExportVideoSegmentPlan> VideoSegments { get; }

    public bool IsSequence => !VideoSegments.IsDefaultOrEmpty;

    public MediaTime SequenceTimelineStart { get; }

    public int? AudioStreamIndex => AudioTracks.Length == 1 ? AudioTracks[0].StreamIndex : null;

    public CropRegion Crop { get; }

    public ImmutableArray<MediaRange> SourceRanges { get; }

    public ExportPreset Preset { get; }

    public bool ReplaceExistingDestination { get; }

    public ExportStrategy Strategy => ExportStrategy.ExactTranscode;

    public PixelSize OutputSize => _outputSize;

    public MediaTime ExpectedDuration =>
        IsSequence
            ? VideoSegments.Aggregate(MediaTime.Zero, static (total, segment) => total + segment.SourceRange.Duration)
            : SourceRanges.Aggregate(MediaTime.Zero, static (total, range) => total + range.Duration);

    private static void ValidateRanges(ImmutableArray<MediaRange> ranges)
    {
        var previousEnd = MediaTime.Zero;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.IsEmpty || range.Start < MediaTime.Zero)
            {
                throw new ArgumentException("Export ranges must be non-empty and non-negative.", nameof(ranges));
            }

            if (index > 0 && range.Start < previousEnd)
            {
                throw new ArgumentException("Export ranges must be ordered and non-overlapping.", nameof(ranges));
            }

            previousEnd = range.End;
        }
    }

    private static bool HasDuplicateAudioTracks(ImmutableArray<ExportAudioTrackPlan> tracks)
    {
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var left = 0; left < tracks.Length; left++)
        {
            for (var right = left + 1; right < tracks.Length; right++)
            {
                if (tracks[left].StreamIndex == tracks[right].StreamIndex &&
                    string.Equals(
                        tracks[left].ExternalSourcePath,
                        tracks[right].ExternalSourcePath,
                        pathComparison))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public sealed record ExportVideoSegmentPlan
{
    public ExportVideoSegmentPlan(
        string sourcePath,
        int videoStreamIndex,
        MediaRange sourceRange,
        CropRegion crop,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (!Path.IsPathFullyQualified(sourcePath) || sourceRange.IsEmpty || sourceRange.Start < MediaTime.Zero)
        {
            throw new ArgumentException("A video segment source and range must be valid.");
        }

        var embeddedTracks = audioTracks.IsDefault ? [] : audioTracks;
        if (embeddedTracks.Any(track => track is null || track.IsExternal) ||
            embeddedTracks.Select(track => track.StreamIndex).Distinct().Count() != embeddedTracks.Length)
        {
            throw new ArgumentException(
                "Segment audio tracks must be distinct embedded streams.",
                nameof(audioTracks));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        VideoStreamIndex = videoStreamIndex;
        SourceRange = sourceRange;
        Crop = crop;
        AudioTracks = embeddedTracks;
    }

    public string SourcePath { get; }

    public int VideoStreamIndex { get; }

    public MediaRange SourceRange { get; }

    public CropRegion Crop { get; }

    public ImmutableArray<ExportAudioTrackPlan> AudioTracks { get; }
}

public sealed record ExportAudioTrackPlan
{
    public ExportAudioTrackPlan(int streamIndex, double gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        if (!double.IsFinite(gainDb) || gainDb is < -60 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        }

        StreamIndex = streamIndex;
        GainDb = gainDb;
    }

    public ExportAudioTrackPlan(int streamIndex, double gainDb, SourceEdit audioEdit)
        : this(streamIndex, gainDb)
    {
        AudioEdit = audioEdit ?? throw new ArgumentNullException(nameof(audioEdit));
    }

    public ExportAudioTrackPlan(string externalSourcePath, int streamIndex, double gainDb)
        : this(externalSourcePath, streamIndex, gainDb, MediaTime.Zero)
    {
    }

    public ExportAudioTrackPlan(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        MediaTime timelineOffset)
        : this(streamIndex, gainDb)
    {
        if (timelineOffset < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timelineOffset),
                "An external audio timeline offset cannot be negative.");
        }

        ExternalSourcePath = ValidateExternalSourcePath(externalSourcePath);
        TimelineOffset = timelineOffset;
    }

    public ExportAudioTrackPlan(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        MediaTime timelineOffset,
        SourceEdit audioEdit)
        : this(externalSourcePath, streamIndex, gainDb, timelineOffset)
    {
        AudioEdit = audioEdit ?? throw new ArgumentNullException(nameof(audioEdit));
    }

    public string? ExternalSourcePath { get; }

    public bool IsExternal => ExternalSourcePath is not null;

    public MediaTime TimelineOffset { get; }

    public SourceEdit? AudioEdit { get; }

    public int StreamIndex { get; }

    public double GainDb { get; }

    private static string ValidateExternalSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("An external export audio path must be absolute.", nameof(sourcePath));
        }

        return Path.GetFullPath(sourcePath);
    }
}

public sealed class ExportPlanException(string message) : Exception(message);
