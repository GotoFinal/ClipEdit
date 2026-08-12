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
    }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public int VideoStreamIndex { get; }

    public ImmutableArray<ExportAudioTrackPlan> AudioTracks { get; }

    public int? AudioStreamIndex => AudioTracks.Length == 1 ? AudioTracks[0].StreamIndex : null;

    public CropRegion Crop { get; }

    public ImmutableArray<MediaRange> SourceRanges { get; }

    public ExportPreset Preset { get; }

    public bool ReplaceExistingDestination { get; }

    public ExportStrategy Strategy => ExportStrategy.ExactTranscode;

    public PixelSize OutputSize => Crop.ExportSize;

    public MediaTime ExpectedDuration =>
        SourceRanges.Aggregate(MediaTime.Zero, static (total, range) => total + range.Duration);

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
