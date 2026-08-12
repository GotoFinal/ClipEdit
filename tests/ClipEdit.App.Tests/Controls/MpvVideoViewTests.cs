using ClipEdit.App.Controls;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.Tests.Controls;

public sealed class MpvVideoViewTests
{
    private static readonly MediaRange[] Ranges =
    [
        new MediaRange(new MediaTime(2, 1), new MediaTime(5, 1)),
        new MediaRange(new MediaTime(8, 1), new MediaTime(12, 1)),
    ];

    [Fact]
    public void Position_inside_kept_range_continues()
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(3, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.Continue, decision.Action);
        Assert.Null(decision.Target);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    public void Position_before_next_kept_range_seeks_to_its_start(int position, int expected)
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(position, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.Seek, decision.Action);
        Assert.Equal(new MediaTime(expected, 1), decision.Target);
    }

    [Fact]
    public void Position_at_final_end_completes_edited_playback()
    {
        var decision = MpvVideoView.GetPlaybackRangeDecision(new MediaTime(12, 1), Ranges);

        Assert.Equal(PlaybackRangeAction.End, decision.Action);
        Assert.Null(decision.Target);
    }
}
