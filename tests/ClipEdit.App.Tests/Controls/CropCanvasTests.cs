using ClipEdit.App.Controls;
using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.Tests.Controls;

public sealed class CropCanvasTests
{
    [Fact]
    public void Move_drag_clamps_without_changing_export_size()
    {
        var start = new CropRegion(new PixelSize(1_920, 1_080), 420, 0, 1_080, 1_080);

        var result = CropCanvas.ApplyDrag(start, CropDragMode.Move, 1_000, 50);

        Assert.Equal(840, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(start.ExportSize, result.ExportSize);
    }

    [Fact]
    public void Corner_drag_resizes_both_edges_and_clamps()
    {
        var start = new CropRegion(new PixelSize(1_920, 1_080), 420, 100, 1_000, 800);

        var result = CropCanvas.ApplyDrag(
            start,
            CropDragMode.Left | CropDragMode.Top,
            -1_000,
            -1_000);

        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1_420, result.Width);
        Assert.Equal(900, result.Height);
    }

    [Fact]
    public void Edge_drag_keeps_at_least_one_source_pixel()
    {
        var start = new CropRegion(new PixelSize(100, 100), 10, 10, 50, 50);

        var result = CropCanvas.ApplyDrag(start, CropDragMode.Left, 1_000, 0);

        Assert.Equal(59, result.X);
        Assert.Equal(1, result.Width);
    }
}
