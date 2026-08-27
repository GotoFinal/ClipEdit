using ClipEdit.App.Controls;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

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

    [AvaloniaFact]
    public void Single_click_moves_only_the_playhead_while_drag_creates_a_selection()
    {
        var timeline = new SequenceTimelineCanvas
        {
            Width = 400,
            Height = 80,
            Duration = 100,
            SelectionStart = 20,
            SelectionEnd = 80,
        };
        var window = new Window
        {
            Width = 400,
            Height = 80,
            WindowDecorations = WindowDecorations.None,
            Content = timeline,
        };
        window.Show();

        window.MouseDown(new Avalonia.Point(100, 50), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Avalonia.Point(100, 50), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(25, timeline.Playhead, 6);
        Assert.Equal(20, timeline.SelectionStart, 6);
        Assert.Equal(80, timeline.SelectionEnd, 6);

        window.MouseDown(new Avalonia.Point(100, 50), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Avalonia.Point(200, 50), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Avalonia.Point(200, 50), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(25, timeline.SelectionStart, 6);
        Assert.Equal(50, timeline.SelectionEnd, 6);
        Assert.Equal(50, timeline.Playhead, 6);
        window.Close();
    }

    [Theory]
    [InlineData(100, 102.9, false)]
    [InlineData(100, 103, true)]
    [InlineData(100, 90, true)]
    public void Selection_drag_uses_a_small_pointer_threshold(double start, double current, bool expected) =>
        Assert.Equal(expected, SequenceTimelineCanvas.HasExceededSelectionDragThreshold(start, current));
}
