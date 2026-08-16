using System.Collections.Immutable;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public interface IKeyframeProbe
{
    Task<KeyframeIndex> ProbeKeyframesAsync(
        string sourcePath,
        int videoStreamIndex,
        MediaTime timestampOrigin,
        MediaTime? sourceDuration,
        CancellationToken cancellationToken = default);
}

public sealed record KeyframeIndex
{
    public KeyframeIndex(int videoStreamIndex, ImmutableArray<MediaTime> timestamps)
        : this(
            videoStreamIndex,
            timestamps.Select(static timestamp => new KeyframePoint(timestamp, null)).ToImmutableArray())
    {
    }

    private KeyframeIndex(
        int videoStreamIndex,
        ImmutableArray<KeyframePoint> points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (points.IsDefault || points.Any(point =>
                point is null ||
                point.PresentationTimestamp < MediaTime.Zero ||
                point.DecodeTimestamp is { } dts && dts > point.PresentationTimestamp))
        {
            throw new ArgumentException(
                "Keyframe points require non-negative presentation timestamps and decode timestamps no later than presentation.",
                nameof(points));
        }

        var ordered = points
            .GroupBy(static point => point.PresentationTimestamp)
            .Select(static group => group.FirstOrDefault(static point => point.DecodeTimestamp is not null) ?? group.First())
            .OrderBy(static point => point.PresentationTimestamp)
            .ToImmutableArray();
        VideoStreamIndex = videoStreamIndex;
        Points = ordered;
        Timestamps = ordered.Select(static point => point.PresentationTimestamp).ToImmutableArray();
    }

    public static KeyframeIndex FromPoints(
        int videoStreamIndex,
        ImmutableArray<KeyframePoint> points) =>
        new(videoStreamIndex, points);

    public int VideoStreamIndex { get; }

    public ImmutableArray<KeyframePoint> Points { get; }

    public ImmutableArray<MediaTime> Timestamps { get; }
}

public sealed record KeyframePoint(MediaTime PresentationTimestamp, MediaTime? DecodeTimestamp);
