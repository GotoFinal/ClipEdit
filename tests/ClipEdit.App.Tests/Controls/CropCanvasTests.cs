using ClipEdit.App.Controls;
using ClipEdit.Domain.Geometry;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaPoint = Avalonia.Point;
using AvaloniaRect = Avalonia.Rect;

namespace ClipEdit.App.Tests.Controls;

public sealed class CropCanvasTests
{
    [AvaloniaFact]
    public void Mouse_drag_from_the_visually_transparent_crop_interior_moves_the_crop()
    {
        var cropCanvas = new CropCanvas
        {
            Width = 400,
            Height = 200,
            IsOverlayOnly = true,
            SourceSize = new PixelSize(200, 100),
            Crop = new CropRegion(new PixelSize(200, 100), 50, 25, 100, 50),
        };
        var previewReceivedPress = false;
        var preview = new Border { Background = Avalonia.Media.Brushes.Black };
        preview.PointerPressed += (_, _) => previewReceivedPress = true;
        var layeredPreview = new Grid();
        layeredPreview.Children.Add(preview);
        layeredPreview.Children.Add(cropCanvas);
        var window = new Window
        {
            Width = 400,
            Height = 200,
            WindowDecorations = WindowDecorations.None,
            Content = layeredPreview,
        };
        window.Show();
        Assert.Equal(new Avalonia.Size(400, 200), cropCanvas.Bounds.Size);

        window.MouseDown(
            new Avalonia.Point(200, 100),
            MouseButton.Left,
            RawInputModifiers.LeftMouseButton);
        window.MouseMove(
            new Avalonia.Point(240, 120),
            RawInputModifiers.LeftMouseButton);
        window.MouseUp(
            new Avalonia.Point(240, 120),
            MouseButton.Left,
            RawInputModifiers.None);

        Assert.Equal(new CropRegion(new PixelSize(200, 100), 70, 35, 100, 50), cropCanvas.Crop);
        Assert.False(previewReceivedPress);
        window.Close();
    }

    [Fact]
    public void Pointer_inside_crop_body_selects_move_drag()
    {
        var mode = CropCanvas.GetDragMode(
            new AvaloniaPoint(100, 80),
            new AvaloniaRect(20, 20, 160, 120));

        Assert.Equal(CropDragMode.Move, mode);
    }

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

    [Fact]
    public void Locked_corner_resize_preserves_ratio_and_the_opposite_corner()
    {
        var start = new CropRegion(new PixelSize(1_920, 1_080), 320, 180, 1_280, 720);

        var result = CropCanvas.ApplyDrag(
            start,
            CropDragMode.Right | CropDragMode.Bottom,
            -320,
            -10,
            preserveAspectRatio: true);

        Assert.Equal(start.X, result.X);
        Assert.Equal(start.Y, result.Y);
        Assert.Equal(16 / 9d, result.Width / (double)result.Height, precision: 2);
        Assert.True(result.Width < start.Width);
        Assert.True(result.Height < start.Height);
    }
}
