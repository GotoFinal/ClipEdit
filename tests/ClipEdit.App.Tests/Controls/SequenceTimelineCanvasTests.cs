using ClipEdit.App.Controls;
using Avalonia.Input;

namespace ClipEdit.App.Tests.Controls;

public sealed class SequenceTimelineCanvasTests
{
    [Fact]
    public void Nearby_clip_edge_snaps_to_make_an_exact_join()
    {
        var snapped = SequenceTimelineCanvas.SnapTimelineStart(
            requestedStart: 9.86,
            clipDuration: 5,
            otherClips: [(15, 22)],
            tolerance: 0.2);

        Assert.Equal(10, snapped, 6);
    }

    [Fact]
    public void Clip_outside_the_snap_tolerance_keeps_its_requested_position()
    {
        var snapped = SequenceTimelineCanvas.SnapTimelineStart(
            requestedStart: 9.5,
            clipDuration: 5,
            otherClips: [(15, 22)],
            tolerance: 0.2);

        Assert.Equal(9.5, snapped, 6);
    }

    [Fact]
    public void Snap_candidate_that_would_overlap_another_clip_is_ignored()
    {
        var snapped = SequenceTimelineCanvas.SnapTimelineStart(
            requestedStart: 10.1,
            clipDuration: 6,
            otherClips: [(5, 10), (15, 20)],
            tolerance: 0.2);

        Assert.Equal(10.1, snapped, 6);
    }

    [Theory]
    [InlineData(false, KeyModifiers.None, false)]
    [InlineData(false, KeyModifiers.Control, true)]
    [InlineData(true, KeyModifiers.None, true)]
    [InlineData(true, KeyModifiers.Control, true)]
    public void Range_mode_selects_by_default_and_control_temporarily_moves(
        bool moveByDefault,
        KeyModifiers modifiers,
        bool expectedMove)
    {
        Assert.Equal(expectedMove, SequenceTimelineCanvas.ShouldMoveClip(moveByDefault, modifiers));
    }
}
