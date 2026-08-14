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
    public void Rotated_non_uniform_outline_uses_preview_transform_order()
    {
        var sourceSize = new DomainPixelSize(1_920, 1_080);
        var canvasSize = sourceSize;
        var transform = new ClipCanvasTransform(0, 0, 0.5, 1, 90);

        var corners = ClipTransformCanvas.GetTransformedCorners(sourceSize, canvasSize, transform);

        Assert.Equal(new Point(1_230, -420), corners[0]);
        Assert.Equal(new Point(1_230, 1_500), corners[1]);
        Assert.Equal(new Point(690, 1_500), corners[2]);
        Assert.Equal(new Point(690, -420), corners[3]);
    }

    [Fact]
    public void Rotated_resize_keeps_opposite_handle_fixed_in_preview_geometry()
    {
        var sourceSize = new DomainPixelSize(1_920, 1_080);
        var canvasSize = sourceSize;
        var start = new ClipCanvasTransform(0, 0, 0.5, 1, 17);
        var before = ClipTransformCanvas.GetTransformedCorners(sourceSize, canvasSize, start);
        var oppositeBefore = Midpoint(before[3], before[0]);

        var result = ClipTransformCanvas.ApplyResize(
            start,
            sourceSize,
            ClipTransformDragMode.Right,
            80,
            30,
            preserveAspectRatio: false);
        var after = ClipTransformCanvas.GetTransformedCorners(sourceSize, canvasSize, result);
        var oppositeAfter = Midpoint(after[3], after[0]);

        Assert.Equal(oppositeBefore.X, oppositeAfter.X, 6);
        Assert.Equal(oppositeBefore.Y, oppositeAfter.Y, 6);
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
    public void Interactive_resize_and_zoom_do_not_clear_clip_mirroring()
    {
        var start = new ClipCanvasTransform(
            0,
            0,
            1,
            0,
            isHorizontallyMirrored: true,
            isVerticallyMirrored: true);

        var zoomed = ClipTransformCanvas.ApplyZoomAt(
            start,
            new Point(960, 540),
            new DomainPixelSize(1_920, 1_080),
            1.1);
        var resized = ClipTransformCanvas.ApplyResize(
            zoomed,
            new DomainPixelSize(1_920, 1_080),
            ClipTransformDragMode.Right,
            100,
            0,
            preserveAspectRatio: true);

        Assert.True(resized.IsHorizontallyMirrored);
        Assert.True(resized.IsVerticallyMirrored);
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

    private static Point Midpoint(Point left, Point right) =>
        new((left.X + right.X) / 2, (left.Y + right.Y) / 2);
}
