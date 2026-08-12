using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ClipEdit.App.Controls;
using ClipEdit.App.ViewModels;
using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.Tests.Controls;

public sealed class AutoCanvasTests
{
    [Theory]
    [InlineData((int)ClipTransformDragMode.Move, (int)CropDragMode.Move, false, (int)CanvasAutoDragTarget.Crop)]
    [InlineData((int)ClipTransformDragMode.Move, (int)CropDragMode.Move, true, (int)CanvasAutoDragTarget.Clip)]
    [InlineData((int)ClipTransformDragMode.None, (int)CropDragMode.Move, true, (int)CanvasAutoDragTarget.Clip)]
    [InlineData((int)ClipTransformDragMode.Move, (int)CropDragMode.None, false, (int)CanvasAutoDragTarget.Clip)]
    [InlineData((int)ClipTransformDragMode.Right, (int)CropDragMode.Move, false, (int)CanvasAutoDragTarget.Clip)]
    [InlineData((int)ClipTransformDragMode.None, (int)CropDragMode.None, false, (int)CanvasAutoDragTarget.None)]
    public void Auto_mode_routes_crop_body_control_override_clip_handles_and_outside_clip(
        int clipMode,
        int cropMode,
        bool controlPressed,
        int expected)
    {
        Assert.Equal(
            (CanvasAutoDragTarget)expected,
            AutoCanvas.ResolveDragTarget((ClipTransformDragMode)clipMode, (CropDragMode)cropMode, controlPressed));
    }

    [AvaloniaFact]
    public void Drag_inside_crop_moves_shared_crop_by_default()
    {
        var canvasSize = new PixelSize(200, 100);
        var control = CreateControl(canvasSize);
        var window = Show(control);

        window.MouseDown(new Avalonia.Point(200, 100), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Avalonia.Point(240, 120), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Avalonia.Point(240, 120), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new CropRegion(canvasSize, 70, 35, 100, 50), control.Crop);
        Assert.Equal(ClipCanvasTransform.Identity, control.Transform);
        window.Close();
    }

    [AvaloniaFact]
    public void Drag_on_clip_outside_crop_moves_the_clip()
    {
        var canvasSize = new PixelSize(200, 100);
        var control = CreateControl(canvasSize);
        var window = Show(control);

        window.MouseDown(new Avalonia.Point(20, 100), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Avalonia.Point(60, 100), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Avalonia.Point(60, 100), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new CropRegion(canvasSize, 50, 25, 100, 50), control.Crop);
        Assert.Equal(20, control.Transform.OffsetX);
        Assert.Equal(0, control.Transform.OffsetY);
        window.Close();
    }

    [AvaloniaFact]
    public void Control_drag_inside_crop_moves_clip_instead_of_crop()
    {
        var canvasSize = new PixelSize(200, 100);
        var control = CreateControl(canvasSize);
        var window = Show(control);
        var held = RawInputModifiers.LeftMouseButton | RawInputModifiers.Control;

        window.MouseDown(new Avalonia.Point(200, 100), MouseButton.Left, held);
        window.MouseMove(new Avalonia.Point(240, 120), held);
        window.MouseUp(
            new Avalonia.Point(240, 120),
            MouseButton.Left,
            RawInputModifiers.Control);

        Assert.Equal(new CropRegion(canvasSize, 50, 25, 100, 50), control.Crop);
        Assert.Equal(20, control.Transform.OffsetX);
        Assert.Equal(10, control.Transform.OffsetY);
        window.Close();
    }

    [Fact]
    public void View_model_exposes_auto_as_a_distinct_combined_tool()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);

        viewModel.UseAutoTool();

        Assert.True(viewModel.IsAutoToolActive);
        Assert.True(viewModel.IsClipTransformOverlayActive);
        Assert.False(viewModel.IsCropToolActive);
        Assert.False(viewModel.IsTransformToolActive);
    }

    private static AutoCanvas CreateControl(PixelSize canvasSize) =>
        new()
        {
            Width = 400,
            Height = 200,
            SourceSize = canvasSize,
            CanvasSize = canvasSize,
            Crop = new CropRegion(canvasSize, 50, 25, 100, 50),
            Transform = ClipCanvasTransform.Identity,
            CropSizeStep = 2,
        };

    private static Window Show(Control control)
    {
        var window = new Window
        {
            Width = 400,
            Height = 200,
            WindowDecorations = WindowDecorations.None,
            Content = control,
        };
        window.Show();
        return window;
    }
}
