using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Domain.Tests.Editing;

public sealed class SequenceClipTests
{
    private readonly Guid _sourceId = Guid.NewGuid();

    [Fact]
    public void Split_creates_two_instances_with_shared_recoverable_source_handles()
    {
        var clip = CreateClip(2, 12, 0, 20, timelineStart: 4);
        var rightId = Guid.NewGuid();

        var (left, right) = clip.Split(Seconds(7), rightId);

        Assert.Equal(Range(2, 7), left.SourceRange);
        Assert.Equal(Range(7, 12), right.SourceRange);
        Assert.Equal(clip.AvailableRange, left.AvailableRange);
        Assert.Equal(clip.AvailableRange, right.AvailableRange);
        Assert.Equal(clip.Id, left.Id);
        Assert.Equal(rightId, right.Id);
        Assert.Equal(Seconds(4), left.TimelineStart);
        Assert.Equal(Seconds(9), right.TimelineStart);
        Assert.Equal(Seconds(14), right.TimelineEnd);
    }

    [Fact]
    public void Removing_an_internal_range_splits_the_clip_and_hides_the_removed_section()
    {
        var clip = CreateClip(2, 12, 0, 20, timelineStart: 4);

        var result = clip.Remove(Range(5, 9), Guid.NewGuid());

        Assert.Equal(2, result.Count);
        Assert.Equal(Range(2, 5), result[0].SourceRange);
        Assert.Equal(Range(9, 12), result[1].SourceRange);
        Assert.All(result, part => Assert.Equal(Range(0, 20), part.AvailableRange));
        Assert.Equal(Seconds(4), result[0].TimelineStart);
        Assert.Equal(Seconds(11), result[1].TimelineStart);
    }

    [Fact]
    public void Keeping_a_section_hides_both_sides_but_trim_can_reveal_them_again()
    {
        var clip = CreateClip(2, 12, 0, 20);

        var kept = clip.KeepOnly(Range(5, 9));

        Assert.NotNull(kept);
        Assert.Equal(Range(5, 9), kept.SourceRange);
        Assert.True(kept.HasHeadHandle);
        Assert.True(kept.HasTailHandle);
        Assert.Equal(Seconds(3), kept.TimelineStart);
        Assert.Equal(Range(3, 9), kept.TrimStart(Seconds(3)).SourceRange);
        Assert.Equal(Seconds(1), kept.TrimStart(Seconds(3)).TimelineStart);
        Assert.Equal(Range(5, 14), kept.TrimEnd(Seconds(14)).SourceRange);
    }

    [Fact]
    public void Removing_a_range_outside_the_clip_does_not_change_it()
    {
        var clip = CreateClip(2, 12, 0, 20);

        var result = clip.Remove(Range(15, 18), Guid.NewGuid());

        Assert.Same(clip, Assert.Single(result));
    }

    [Fact]
    public void Trim_cannot_cross_the_other_edge_or_source_handles()
    {
        var clip = CreateClip(2, 12, 0, 20);

        Assert.Throws<ArgumentOutOfRangeException>(() => clip.TrimStart(Seconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.TrimStart(Seconds(12)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.TrimEnd(Seconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.TrimEnd(Seconds(21)));
    }

    [Fact]
    public void Move_changes_only_timeline_placement_and_rejects_negative_time()
    {
        var clip = CreateClip(2, 12, 0, 20, timelineStart: 4);

        var moved = clip.MoveTo(Seconds(15));

        Assert.Equal(Seconds(15), moved.TimelineStart);
        Assert.Equal(Seconds(25), moved.TimelineEnd);
        Assert.Equal(clip.SourceRange, moved.SourceRange);
        Assert.Equal(clip.AvailableRange, moved.AvailableRange);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.MoveTo(Seconds(-1)));
    }
    [Fact]
    public void Audio_gain_is_bounded_and_survives_non_destructive_clip_operations()
    {
        var clip = new SequenceClip(
            Guid.NewGuid(),
            _sourceId,
            Range(2, 12),
            Range(0, 20),
            Seconds(4),
            audioGainDb: -5.5);

        var (left, right) = clip.Split(Seconds(7), Guid.NewGuid());

        Assert.Equal(-5.5, left.AudioGainDb);
        Assert.Equal(-5.5, right.AudioGainDb);
        Assert.Equal(-5.5, clip.TrimStart(Seconds(3)).AudioGainDb);
        Assert.Equal(-5.5, clip.TrimEnd(Seconds(14)).AudioGainDb);
        Assert.Equal(-5.5, clip.MoveTo(Seconds(20)).AudioGainDb);
        Assert.Equal(8.25, clip.WithAudioGain(8.25).AudioGainDb);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.WithAudioGain(12.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.WithAudioGain(double.NaN));
    }

    [Fact]
    public void Playback_speed_maps_source_time_to_a_scaled_timeline_and_survives_edits()
    {
        var clip = new SequenceClip(
            Guid.NewGuid(),
            _sourceId,
            Range(2, 12),
            Range(0, 20),
            Seconds(4),
            playbackSpeedPercent: 200);

        Assert.Equal(Seconds(5), clip.Duration);
        Assert.Equal(Seconds(9), clip.TimelineEnd);
        Assert.Equal(new MediaTime(13, 2), clip.SourceTimeToTimeline(Seconds(7)));
        Assert.Equal(Seconds(7), clip.TimelineTimeToSource(new MediaTime(13, 2)));

        var (left, right) = clip.Split(Seconds(8), Guid.NewGuid());
        Assert.Equal(Seconds(3), left.Duration);
        Assert.Equal(Seconds(7), right.TimelineStart);
        Assert.Equal(200, right.PlaybackSpeedPercent);
        Assert.Equal(200, clip.TrimStart(Seconds(4)).PlaybackSpeedPercent);
        Assert.Equal(200, clip.MoveTo(Seconds(20)).PlaybackSpeedPercent);
    }

    [Fact]
    public void Playback_speed_is_bounded()
    {
        var clip = CreateClip(0, 10, 0, 10);

        Assert.Equal(1, clip.WithPlaybackSpeed(1).PlaybackSpeedPercent);
        Assert.Equal(10_000, clip.WithPlaybackSpeed(10_000).PlaybackSpeedPercent);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.WithPlaybackSpeed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.WithPlaybackSpeed(10_001));
    }


    private SequenceClip CreateClip(
        int start,
        int end,
        int availableStart,
        int availableEnd,
        int timelineStart = 0) =>
        new(
            Guid.NewGuid(),
            _sourceId,
            Range(start, end),
            Range(availableStart, availableEnd),
            Seconds(timelineStart));

    private static MediaRange Range(int start, int end) => new(Seconds(start), Seconds(end));

    private static MediaTime Seconds(int value) => new(value, 1);
}
