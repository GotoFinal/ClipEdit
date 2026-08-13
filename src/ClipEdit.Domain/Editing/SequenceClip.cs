using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Editing;

/// <summary>
/// A non-destructive timeline instance that refers to a range of one source asset.
/// The available range records the media handles that may be revealed by trimming.
/// </summary>
public sealed record SequenceClip
{
    public SequenceClip(
        Guid id,
        Guid sourceId,
        MediaRange sourceRange,
        MediaRange availableRange,
        MediaTime timelineStart = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A clip id cannot be empty.", nameof(id));
        }

        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source id cannot be empty.", nameof(sourceId));
        }

        if (sourceRange.IsEmpty)
        {
            throw new ArgumentException("A sequence clip cannot be empty.", nameof(sourceRange));
        }

        if (availableRange.IsEmpty ||
            sourceRange.Start < availableRange.Start ||
            sourceRange.End > availableRange.End)
        {
            throw new ArgumentException(
                "The clip range must be contained by its non-empty available range.",
                nameof(availableRange));
        }

        if (timelineStart < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineStart));
        }

        Id = id;
        SourceId = sourceId;
        SourceRange = sourceRange;
        AvailableRange = availableRange;
        TimelineStart = timelineStart;
    }

    public Guid Id { get; }

    public Guid SourceId { get; }

    public MediaRange SourceRange { get; }

    public MediaRange AvailableRange { get; }

    public MediaTime TimelineStart { get; }

    public MediaTime TimelineEnd => TimelineStart + Duration;

    public MediaTime Duration => SourceRange.Duration;

    public bool HasHeadHandle => SourceRange.Start > AvailableRange.Start;

    public bool HasTailHandle => SourceRange.End < AvailableRange.End;

    public (SequenceClip Left, SequenceClip Right) Split(MediaTime sourceTime, Guid rightClipId)
    {
        if (sourceTime <= SourceRange.Start || sourceTime >= SourceRange.End)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceTime),
                sourceTime,
                "A split point must be strictly inside the visible clip range.");
        }

        return (
            WithRange(new MediaRange(SourceRange.Start, sourceTime)),
            new SequenceClip(
                rightClipId,
                SourceId,
                new MediaRange(sourceTime, SourceRange.End),
                AvailableRange,
                TimelineStart + (sourceTime - SourceRange.Start)));
    }

    public IReadOnlyList<SequenceClip> Remove(MediaRange removal, Guid rightClipId)
    {
        var start = Max(SourceRange.Start, removal.Start);
        var end = Min(SourceRange.End, removal.End);
        if (end <= start)
        {
            return [this];
        }

        var keepsLeft = start > SourceRange.Start;
        var keepsRight = end < SourceRange.End;
        if (!keepsLeft && !keepsRight)
        {
            return [];
        }

        if (keepsLeft && keepsRight)
        {
            return
            [
                WithRange(new MediaRange(SourceRange.Start, start)),
                new SequenceClip(
                    rightClipId,
                    SourceId,
                    new MediaRange(end, SourceRange.End),
                    AvailableRange,
                    TimelineStart + (end - SourceRange.Start)),
            ];
        }

        return
        [
            WithRange(keepsLeft
                ? new MediaRange(SourceRange.Start, start)
                : new MediaRange(end, SourceRange.End)),
        ];
    }

    public SequenceClip? KeepOnly(MediaRange selection)
    {
        var start = Max(SourceRange.Start, selection.Start);
        var end = Min(SourceRange.End, selection.End);
        return end <= start ? null : WithRange(new MediaRange(start, end));
    }

    public SequenceClip TrimStart(MediaTime sourceTime)
    {
        if (sourceTime < AvailableRange.Start || sourceTime >= SourceRange.End)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTime));
        }

        return WithRange(new MediaRange(sourceTime, SourceRange.End));
    }

    public SequenceClip TrimEnd(MediaTime sourceTime)
    {
        if (sourceTime <= SourceRange.Start || sourceTime > AvailableRange.End)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTime));
        }

        return WithRange(new MediaRange(SourceRange.Start, sourceTime));
    }

    public SequenceClip MoveTo(MediaTime timelineStart)
    {
        return timelineStart == TimelineStart
            ? this
            : new SequenceClip(Id, SourceId, SourceRange, AvailableRange, timelineStart);
    }

    private SequenceClip WithRange(MediaRange range)
    {
        var proposedTimelineStart = TimelineStart + (range.Start - SourceRange.Start);
        var timelineStart = proposedTimelineStart < MediaTime.Zero ? MediaTime.Zero : proposedTimelineStart;
        return new SequenceClip(Id, SourceId, range, AvailableRange, timelineStart);
    }

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;

    private static MediaTime Max(MediaTime left, MediaTime right) => left >= right ? left : right;
}
