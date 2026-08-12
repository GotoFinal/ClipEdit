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
}
