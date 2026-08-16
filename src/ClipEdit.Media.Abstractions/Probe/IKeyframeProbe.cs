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
    {
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        if (timestamps.IsDefault || timestamps.Any(timestamp => timestamp < MediaTime.Zero))
        {
            throw new ArgumentException(
                "Keyframe timestamps must be initialized and non-negative.",
                nameof(timestamps));
        }

        var ordered = timestamps
            .Distinct()
            .Order()
            .ToImmutableArray();
        VideoStreamIndex = videoStreamIndex;
        Timestamps = ordered;
    }

    public int VideoStreamIndex { get; }

    public ImmutableArray<MediaTime> Timestamps { get; }
}
