using Avalonia;
using ClipEdit.App.Controls;
using ClipEdit.Domain.Geometry;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Tests.Controls;

public sealed class ClipTransformCanvasTests
{
    [Fact]
    public void Drag_moves_only_clip_content_on_the_canvas()
    {
        var start = new ClipCanvasTransform(20, -10, 1.5, 15);

        var result = ClipTransformCanvas.ApplyDrag(start, 30, -5);

        Assert.Equal(50, result.OffsetX);
        Assert.Equal(-15, result.OffsetY);
        Assert.Equal(1.5, result.Scale);
        Assert.Equal(15, result.RotationDegrees);
    }

    [Fact]
    public void Pointer_centered_zoom_keeps_canvas_point_over_same_content()
    {
        var start = new ClipCanvasTransform(0, 0, 1, 0);

        var result = ClipTransformCanvas.ApplyZoomAt(
            start,
            new Point(750, 540),
            new DomainPixelSize(1_920, 1_080),
            2);

        Assert.Equal(210, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resize_handle_preserves_clip_ratio_by_default_and_keeps_opposite_edge_fixed()
    {
        var start = ClipCanvasTransform.Identity;

        var result = ClipTransformCanvas.ApplyResize(
            start,
            new DomainPixelSize(1_920, 1_080),
            ClipTransformDragMode.Right,
            192,
            0,
            preserveAspectRatio: true);

        Assert.Equal(1.1, result.ScaleX, 6);
        Assert.Equal(1.1, result.ScaleY, 6);
        Assert.Equal(96, result.OffsetX, 6);
        Assert.Equal(0, result.OffsetY, 6);
    }

    [Fact]
    public void Control_resize_changes_only_the_grabbed_axis()
    {
        var start = ClipCanvasTransform.Identity;

        var result = ClipTransformCanvas.ApplyResize(
            start,
            new DomainPixelSize(1_920, 1_080),
            ClipTransformDragMode.Right,
            192,
            0,
            preserveAspectRatio: false);

        Assert.Equal(1.1, result.ScaleX, 6);
        Assert.Equal(1, result.ScaleY, 6);
        Assert.Equal(96, result.OffsetX, 6);
        Assert.Equal(0, result.OffsetY, 6);
    }

    [Fact]
    public void Rotation_handle_applies_clockwise_angle_around_clip_center()
    {
        var result = ClipTransformCanvas.ApplyRotation(
            ClipCanvasTransform.Identity,
            new Point(0, 0),
            new Point(0, -100),
            new Point(100, 0));

        Assert.Equal(90, result.RotationDegrees);
    }

    [Fact]
    public void Handle_hit_testing_wins_over_clip_body_drag()
    {
        Point[] corners =
        [
            new(100, 100),
            new(300, 100),
            new(300, 200),
            new(100, 200),
        ];

        Assert.Equal(
            ClipTransformDragMode.Right,
            ClipTransformCanvas.GetDragMode(new Point(300, 150), corners, new Point(200, 150)));
        Assert.Equal(
            ClipTransformDragMode.Move,
            ClipTransformCanvas.GetDragMode(new Point(200, 150), corners, new Point(200, 150)));
        Assert.Equal(
            ClipTransformDragMode.None,
            ClipTransformCanvas.GetDragMode(new Point(20, 20), corners, new Point(200, 150)));
    }


    [Fact]
    public void Wheel_sensitivity_uses_configured_zoom_and_one_degree_rotation_defaults()
    {
        Assert.Equal(1.1, ClipTransformCanvas.CalculateWheelZoomFactor(1, 10), 6);
        Assert.Equal(1d / 1.1, ClipTransformCanvas.CalculateWheelZoomFactor(-1, 10), 6);
        Assert.Equal(1, ClipTransformCanvas.CalculateWheelRotationDelta(1, 1));
        Assert.Equal(-1, ClipTransformCanvas.CalculateWheelRotationDelta(-1, 1));
        Assert.Equal(5, ClipTransformCanvas.CalculateWheelRotationDelta(1, 5));
    }
}
