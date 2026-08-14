using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Editing;

/// <summary>
/// A non-destructive timeline instance that refers to a range of one source asset.
/// The available range records the media handles that may be revealed by trimming.
/// </summary>
public sealed record SequenceClip
{
    public const int MinimumPlaybackSpeedPercent = 1;
    public const int MaximumPlaybackSpeedPercent = 10_000;
    public const int DefaultPlaybackSpeedPercent = 100;

    public SequenceClip(
        Guid id,
        Guid sourceId,
        MediaRange sourceRange,
        MediaRange availableRange,
        MediaTime timelineStart = default,
        double audioGainDb = 0,
        int playbackSpeedPercent = DefaultPlaybackSpeedPercent)
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

        if (!double.IsFinite(audioGainDb) || audioGainDb is < -60 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(audioGainDb));
        }

        if (playbackSpeedPercent is < MinimumPlaybackSpeedPercent or > MaximumPlaybackSpeedPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(playbackSpeedPercent));
        }

        Id = id;
        SourceId = sourceId;
        SourceRange = sourceRange;
        AudioGainDb = audioGainDb;
        AvailableRange = availableRange;
        TimelineStart = timelineStart;
        PlaybackSpeedPercent = playbackSpeedPercent;
    }

    public Guid Id { get; }

    public Guid SourceId { get; }

    public MediaRange SourceRange { get; }

    public MediaRange AvailableRange { get; }

    public double AudioGainDb { get; }

    public MediaTime TimelineStart { get; }

    public int PlaybackSpeedPercent { get; }

    public double PlaybackSpeed => PlaybackSpeedPercent / 100d;

    public MediaTime TimelineEnd => TimelineStart + Duration;

    public MediaTime Duration => SourceDurationToTimeline(SourceRange.Duration);

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
                TimelineStart + SourceDurationToTimeline(sourceTime - SourceRange.Start),
                AudioGainDb,
                PlaybackSpeedPercent));
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
                    TimelineStart + SourceDurationToTimeline(end - SourceRange.Start),
                    AudioGainDb,
                    PlaybackSpeedPercent),
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
            : new SequenceClip(
                Id,
                SourceId,
                SourceRange,
                AvailableRange,
                timelineStart,
                AudioGainDb,
                PlaybackSpeedPercent);
    }

    public SequenceClip WithAudioGain(double audioGainDb)
    {
        return audioGainDb == AudioGainDb
            ? this
            : new SequenceClip(
                Id,
                SourceId,
                SourceRange,
                AvailableRange,
                TimelineStart,
                audioGainDb,
                PlaybackSpeedPercent);
    }

    public SequenceClip WithPlaybackSpeed(int playbackSpeedPercent)
    {
        return playbackSpeedPercent == PlaybackSpeedPercent
            ? this
            : new SequenceClip(
                Id,
                SourceId,
                SourceRange,
                AvailableRange,
                TimelineStart,
                AudioGainDb,
                playbackSpeedPercent);
    }

    public MediaTime SourceDurationToTimeline(MediaTime sourceDuration) =>
        sourceDuration * 100 / PlaybackSpeedPercent;

    public MediaTime TimelineDurationToSource(MediaTime timelineDuration) =>
        timelineDuration * PlaybackSpeedPercent / 100;

    public MediaTime SourceTimeToTimeline(MediaTime sourceTime) =>
        TimelineStart + SourceDurationToTimeline(sourceTime - SourceRange.Start);

    public MediaTime TimelineTimeToSource(MediaTime timelineTime) =>
        SourceRange.Start + TimelineDurationToSource(timelineTime - TimelineStart);

    private SequenceClip WithRange(MediaRange range)
    {
        var proposedTimelineStart = TimelineStart + SourceDurationToTimeline(range.Start - SourceRange.Start);
        var timelineStart = proposedTimelineStart < MediaTime.Zero ? MediaTime.Zero : proposedTimelineStart;
        return new SequenceClip(
            Id,
            SourceId,
            range,
            AvailableRange,
            timelineStart,
            AudioGainDb,
            PlaybackSpeedPercent);
    }

    private static MediaTime Min(MediaTime left, MediaTime right) => left <= right ? left : right;

    private static MediaTime Max(MediaTime left, MediaTime right) => left >= right ? left : right;
}
