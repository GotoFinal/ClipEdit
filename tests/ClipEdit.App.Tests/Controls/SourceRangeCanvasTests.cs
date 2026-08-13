using ClipEdit.App.Controls;
using ClipEdit.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace ClipEdit.App.Tests.Controls;

public sealed class SourceRangeCanvasTests
{
    [AvaloniaFact]
    public void Dragging_a_visible_selection_edge_updates_that_boundary_and_playhead()
    {
        var rangeCanvas = new SourceRangeCanvas
        {
            Width = 400,
            Height = 40,
            Duration = 100,
            SelectionStart = 20,
            SelectionEnd = 80,
        };
        var window = new Window
        {
            Width = 400,
            Height = 40,
            WindowDecorations = WindowDecorations.None,
            Content = rangeCanvas,
        };
        window.Show();

        window.MouseDown(
            new Avalonia.Point(80, 20),
            MouseButton.Left,
            RawInputModifiers.LeftMouseButton);
        window.MouseMove(
            new Avalonia.Point(100, 20),
            RawInputModifiers.LeftMouseButton);
        window.MouseUp(
            new Avalonia.Point(100, 20),
            MouseButton.Left,
            RawInputModifiers.None);

        Assert.Equal(25, rangeCanvas.SelectionStart);
        Assert.Equal(80, rangeCanvas.SelectionEnd);
        Assert.Equal(25, rangeCanvas.Playhead);
        window.Close();
    }

    [AvaloniaFact]
    public void Wheel_zoom_keeps_the_time_under_the_pointer_stationary()
    {
        var rangeCanvas = new SourceRangeCanvas
        {
            Width = 400,
            Height = 40,
            Duration = 100,
            SelectionStart = 20,
            SelectionEnd = 80,
        };
        var window = new Window
        {
            Width = 400,
            Height = 40,
            WindowDecorations = WindowDecorations.None,
            Content = rangeCanvas,
        };
        window.Show();

        window.MouseWheel(
            new Avalonia.Point(300, 20),
            new Avalonia.Vector(0, 1),
            RawInputModifiers.None);

        Assert.Equal(1.25, rangeCanvas.Zoom, 6);
        Assert.Equal(15, rangeCanvas.ViewportStart, 6);
        window.Close();
    }

    [AvaloniaFact]
    public void Edge_drag_uses_the_zoomed_visible_time_window()
    {
        var rangeCanvas = new SourceRangeCanvas
        {
            Width = 400,
            Height = 40,
            Duration = 100,
            Zoom = 2,
            ViewportStart = 25,
            SelectionStart = 30,
            SelectionEnd = 60,
        };
        var window = new Window
        {
            Width = 400,
            Height = 40,
            WindowDecorations = WindowDecorations.None,
            Content = rangeCanvas,
        };
        window.Show();

        window.MouseDown(
            new Avalonia.Point(40, 20),
            MouseButton.Left,
            RawInputModifiers.LeftMouseButton);
        window.MouseMove(
            new Avalonia.Point(80, 20),
            RawInputModifiers.LeftMouseButton);
        window.MouseUp(
            new Avalonia.Point(80, 20),
            MouseButton.Left,
            RawInputModifiers.None);

        Assert.Equal(35, rangeCanvas.SelectionStart, 6);
        Assert.Equal(60, rangeCanvas.SelectionEnd, 6);
        Assert.Equal(35, rangeCanvas.Playhead, 6);
        window.Close();
    }

    [Theory]
    [InlineData(2, 8, 2, 8)]
    [InlineData(8, 2, 2, 8)]
    [InlineData(4, 4, 4, 4)]
    public void Drag_selection_is_normalized_in_source_order(
        double anchor,
        double current,
        double expectedStart,
        double expectedEnd)
    {
        var result = SourceRangeCanvas.NormalizeSelection(anchor, current);

        Assert.Equal(expectedStart, result.Start);
        Assert.Equal(expectedEnd, result.End);
    }

    [Theory]
    [InlineData(99, (int)SourceRangeDragMode.StartEdge)]
    [InlineData(201, (int)SourceRangeDragMode.EndEdge)]
    [InlineData(150, (int)SourceRangeDragMode.NewSelection)]
    public void Pointer_near_a_selection_edge_selects_that_trim_handle(
        double pointerX,
        int expected)
    {
        Assert.Equal((SourceRangeDragMode)expected, SourceRangeCanvas.GetDragMode(pointerX, 100, 200));
    }

    [Fact]
    public void Start_edge_drag_changes_only_start_and_previews_that_boundary()
    {
        var result = SourceRangeCanvas.ApplyDrag(
            SourceRangeDragMode.StartEdge,
            anchor: 20,
            selectionStart: 20,
            selectionEnd: 80,
            current: 25.5);

        Assert.Equal((25.5, 80, 25.5), result);
    }

    [Fact]
    public void End_edge_drag_cannot_cross_the_start()
    {
        var result = SourceRangeCanvas.ApplyDrag(
            SourceRangeDragMode.EndEdge,
            anchor: 80,
            selectionStart: 20,
            selectionEnd: 80,
            current: 10);

        Assert.Equal((20, 20, 20), result);
    }

    [Fact]
    public void Viewport_zoom_preserves_an_arbitrary_anchor_position()
    {
        var viewport = TimelineViewportMath.ZoomAround(
            duration: 100,
            currentZoom: 2,
            currentStart: 20,
            requestedZoom: 4,
            anchor: 30);

        Assert.Equal(4, viewport.Zoom);
        Assert.Equal(25, viewport.Start);
    }

    [Fact]
    public void Free_viewport_can_zoom_out_and_pan_the_sequence_to_either_side()
    {
        Assert.Equal(400, TimelineViewportMath.VisibleDuration(100, 0.25, freeViewport: true));
        Assert.Equal(-360, TimelineViewportMath.ClampStart(100, 0.25, -1_000, freeViewport: true));
        Assert.Equal(60, TimelineViewportMath.ClampStart(100, 0.25, 1_000, freeViewport: true));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 1.9952623149688795)]
    [InlineData(-12, 0.5011872336272722)]
    [InlineData(-60, 0.03162277660168379)]
    public void Waveform_height_tracks_effective_audio_gain(double gainDb, double expectedScale)
    {
        Assert.Equal(expectedScale, SourceRangeCanvas.GainToWaveformScale(gainDb), 9);
    }
}
