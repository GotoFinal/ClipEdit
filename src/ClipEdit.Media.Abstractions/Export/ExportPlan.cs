using System.Collections.Immutable;
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
        if (AudioTracks.Any(track => track is null) ||
            AudioTracks.Select(track => track.StreamIndex).Distinct().Count() != AudioTracks.Length)
        {
            throw new ArgumentException("Export audio tracks must be non-null and use distinct stream indices.", nameof(audioTracks));
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

    public int StreamIndex { get; }

    public double GainDb { get; }
}

public sealed class ExportPlanException(string message) : Exception(message);
