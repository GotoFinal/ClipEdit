using System.Collections.Immutable;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Editing;

/// <summary>
/// Non-destructive keep/remove decisions for one source stream timeline.
/// </summary>
public sealed record SourceEdit
{
    public SourceEdit(MediaTime sourceDuration)
        : this(
            sourceDuration,
            sourceDuration > MediaTime.Zero
                ? [new MediaRange(MediaTime.Zero, sourceDuration)]
                : [])
    {
    }

    private SourceEdit(MediaTime sourceDuration, ImmutableArray<MediaRange> keptRanges)
    {
        if (sourceDuration < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDuration),
                sourceDuration,
                "Source duration cannot be negative.");
        }

        ValidateRanges(sourceDuration, keptRanges);
        SourceDuration = sourceDuration;
        KeptRanges = keptRanges;
    }

    public MediaTime SourceDuration { get; }

    public ImmutableArray<MediaRange> KeptRanges { get; }

    public MediaTime OutputDuration =>
        KeptRanges.Aggregate(MediaTime.Zero, static (total, range) => total + range.Duration);

    public bool IsEmpty => KeptRanges.IsEmpty;

    public bool IsUnedited =>
        KeptRanges.Length == 1 &&
        KeptRanges[0].Start == MediaTime.Zero &&
        KeptRanges[0].End == SourceDuration;

    public SourceEdit Remove(MediaRange removal)
    {
        if (removal.IsEmpty || KeptRanges.IsEmpty)
        {
            return this;
        }

        var boundedStart = Max(MediaTime.Zero, removal.Start);
        var boundedEnd = Min(SourceDuration, removal.End);
        if (boundedEnd <= boundedStart)
        {
            return this;
        }

        var kept = ImmutableArray.CreateBuilder<MediaRange>(KeptRanges.Length + 1);
        foreach (var range in KeptRanges)
        {
            if (boundedEnd <= range.Start || boundedStart >= range.End)
            {
                kept.Add(range);
                continue;
            }

            if (boundedStart > range.Start)
            {
                kept.Add(new MediaRange(range.Start, Min(boundedStart, range.End)));
            }

            if (boundedEnd < range.End)
            {
                kept.Add(new MediaRange(Max(boundedEnd, range.Start), range.End));
            }
        }

        var result = kept.ToImmutable();
        return result.SequenceEqual(KeptRanges)
            ? this
            : new SourceEdit(SourceDuration, result);
    }

    public SourceEdit Reset() => new(SourceDuration);

    public bool Contains(MediaTime sourceTime)
    {
        return KeptRanges.Any(range => range.Contains(sourceTime));
    }

    private static void ValidateRanges(
        MediaTime sourceDuration,
        ImmutableArray<MediaRange> ranges)
    {
        var previousEnd = MediaTime.Zero;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.IsEmpty)
            {
                throw new ArgumentException("Kept ranges cannot be empty.", nameof(ranges));
            }

            if (range.Start < MediaTime.Zero || range.End > sourceDuration)
            {
                throw new ArgumentException("A kept range is outside the source duration.", nameof(ranges));
            }

            if (index > 0 && range.Start < previousEnd)
            {
                throw new ArgumentException("Kept ranges must be ordered and non-overlapping.", nameof(ranges));
            }

            previousEnd = range.End;
        }
    }

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;

    private static MediaTime Max(MediaTime left, MediaTime right) => left >= right ? left : right;
}
