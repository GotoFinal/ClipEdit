using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PixelSize _canvasSize = new(1, 1);
    private CropRegion _canvasCrop = CropRegion.FullFrame(new PixelSize(1, 1));
    private CanvasInteractionTool _canvasTool = CanvasInteractionTool.Crop;

    public PixelSize CanvasSize
    {
        get => _canvasSize;
        private set
        {
            if (SetProperty(ref _canvasSize, value))
            {
                OnPropertyChanged(nameof(CanvasSizeText));
            }
        }
    }

    public CropRegion CanvasCrop
    {
        get => _canvasCrop;
        set
        {
            if (value.SourceSize != CanvasSize)
            {
                throw new ArgumentException("The crop must use the project canvas size.", nameof(value));
            }

            var wasResized = value.Width != _canvasCrop.Width || value.Height != _canvasCrop.Height;
            if (!SetProperty(ref _canvasCrop, value))
            {
                return;
            }

            RaiseCanvasCropChanged();
            if (!_isApplyingCropPreset && wasResized && !SelectedCropAspectPreset.IsCustom)
            {
                _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
                OnPropertyChanged(nameof(SelectedCropAspectPreset));
                StatusText = "Crop resized manually; preset changed to Custom";
            }

            MarkProjectDirty();
            RaiseExportStateChanged();
        }
    }

    public int CanvasCropX
    {
        get => CanvasCrop.X;
        set => TrySetCanvasCrop(value, CanvasCrop.Y, CanvasCrop.Width, CanvasCrop.Height);
    }

    public int CanvasCropY
    {
        get => CanvasCrop.Y;
        set => TrySetCanvasCrop(CanvasCrop.X, value, CanvasCrop.Width, CanvasCrop.Height);
    }

    public int CanvasCropWidth
    {
        get => CanvasCrop.Width;
        set => TrySetCanvasCrop(CanvasCrop.X, CanvasCrop.Y, value, CanvasCrop.Height);
    }

    public int CanvasCropHeight
    {
        get => CanvasCrop.Height;
        set => TrySetCanvasCrop(CanvasCrop.X, CanvasCrop.Y, CanvasCrop.Width, value);
    }

    public string CanvasCropSizeText => $"{CanvasCrop.Width} × {CanvasCrop.Height}";
    public int CropSizeStep => SelectedExportPreset.RequiresEvenDimensions ? 2 : 1;


    public string CanvasSizeText => $"{CanvasSize.Width} × {CanvasSize.Height} canvas";

    public CanvasInteractionTool CanvasTool
    {
        get => _canvasTool;
        set
        {
            if (SetProperty(ref _canvasTool, value))
            {
                OnPropertyChanged(nameof(IsCropToolActive));
                OnPropertyChanged(nameof(IsTransformToolActive));
                StatusText = value == CanvasInteractionTool.Crop
                    ? "Crop tool: drag the shared frame or its handles"
                    : "Clip tool: drag the selected clip; wheel zooms; Shift+wheel rotates";
            }
        }
    }

    public bool IsCropToolActive => CanvasTool == CanvasInteractionTool.Crop;

    public bool IsTransformToolActive => CanvasTool == CanvasInteractionTool.Transform;

    public void UseCropTool() => CanvasTool = CanvasInteractionTool.Crop;

    public void UseTransformTool() => CanvasTool = CanvasInteractionTool.Transform;

    public bool ResetCanvasCrop()
    {
        if (VideoClips.Count == 0)
        {
            return false;
        }

        _isApplyingCropPreset = true;
        try
        {
            CanvasCrop = SnapCropSizeCentered(CropRegion.FullFrame(CanvasSize));
            _selectedCropAspectPreset = BuiltInCropAspectPresets.Custom;
            OnPropertyChanged(nameof(SelectedCropAspectPreset));
        }
        finally
        {
            _isApplyingCropPreset = false;
        }

        StatusText = "Shared crop reset to the full canvas";
        return true;
    }

    public bool ResetSelectedClipToFill()
    {
        if (SelectedVideoClip is not { } clip)
        {
            return false;
        }

        clip.CanvasTransform = ClipCanvasTransform.Fill(clip.VideoSize, CanvasSize);
        StatusText = $"Centered {clip.DisplayName} and filled the canvas";
        return true;
    }

    public bool FitSelectedClipToCanvas()
    {
        if (SelectedVideoClip is not { } clip)
        {
            return false;
        }

        clip.CanvasTransform = ClipCanvasTransform.Fit(clip.VideoSize, CanvasSize);
        StatusText = $"Fit all of {clip.DisplayName} inside the canvas";
        return true;
    }

    private void InitializeCanvas(PixelSize size, CropRegion crop)
    {
        if (crop.SourceSize != size)
        {
            throw new ArgumentException("The saved crop must match the saved canvas.", nameof(crop));
        }

        CanvasSize = size;
        _canvasCrop = crop;
        RaiseCanvasCropChanged();
    }

    private void ResetProjectCanvasState()
    {
        CanvasSize = new PixelSize(1, 1);
        _canvasCrop = CropRegion.FullFrame(CanvasSize);
        _canvasTool = CanvasInteractionTool.Crop;
        RaiseCanvasCropChanged();
        OnPropertyChanged(nameof(CanvasTool));
        OnPropertyChanged(nameof(IsCropToolActive));
        OnPropertyChanged(nameof(IsTransformToolActive));
    }

    private void TrySetCanvasCrop(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        if (x < 0 || y < 0 || x >= CanvasSize.Width || y >= CanvasSize.Height)
        {
            return;
        }

        if (width != CanvasCrop.Width)
        {
            width = SnapDimensionDown(width, CanvasSize.Width - x, CropSizeStep);
        }

        if (height != CanvasCrop.Height)
        {
            height = SnapDimensionDown(height, CanvasSize.Height - y, CropSizeStep);
        }


        try
        {
            CanvasCrop = new CropRegion(CanvasSize, x, y, width, height);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }


    private CropRegion SnapCropSizeCentered(CropRegion crop)
    {
        var width = SnapDimensionDown(crop.Width, crop.SourceSize.Width, CropSizeStep);
        var height = SnapDimensionDown(crop.Height, crop.SourceSize.Height, CropSizeStep);
        var centerX = crop.X + (crop.Width / 2d);
        var centerY = crop.Y + (crop.Height / 2d);
        var x = Math.Clamp(
            checked((int)Math.Round(centerX - (width / 2d))),
            0,
            crop.SourceSize.Width - width);
        var y = Math.Clamp(
            checked((int)Math.Round(centerY - (height / 2d))),
            0,
            crop.SourceSize.Height - height);
        return new CropRegion(crop.SourceSize, x, y, width, height);
    }

    private static int SnapDimensionDown(int value, int maximum, int step)
    {
        if (step <= 1 || maximum < step)
        {
            return Math.Clamp(value, 1, maximum);
        }

        var maximumValid = maximum - (maximum % step);
        return Math.Clamp(value - (value % step), step, maximumValid);
    }
    private void RaiseCanvasCropChanged()
    {
        OnPropertyChanged(nameof(CanvasCrop));
        OnPropertyChanged(nameof(CanvasCropX));
        OnPropertyChanged(nameof(CanvasCropY));
        OnPropertyChanged(nameof(CanvasCropWidth));
        OnPropertyChanged(nameof(CanvasCropHeight));
        OnPropertyChanged(nameof(CanvasCropSizeText));
    }

    private static ClipCanvasTransform CreateLegacyCanvasTransform(
        PixelSize sourceSize,
        PixelSize canvasSize,
        CropRegion canvasCrop,
        CropRegion sourceWindow)
    {
        var scale = Math.Min(
            canvasCrop.Width / (double)sourceWindow.Width,
            canvasCrop.Height / (double)sourceWindow.Height);
        var sourceCenterX = sourceSize.Width / 2d;
        var sourceCenterY = sourceSize.Height / 2d;
        var windowCenterX = sourceWindow.X + (sourceWindow.Width / 2d);
        var windowCenterY = sourceWindow.Y + (sourceWindow.Height / 2d);
        var canvasCenterX = canvasSize.Width / 2d;
        var canvasCenterY = canvasSize.Height / 2d;
        var cropCenterX = canvasCrop.X + (canvasCrop.Width / 2d);
        var cropCenterY = canvasCrop.Y + (canvasCrop.Height / 2d);
        return new ClipCanvasTransform(
            cropCenterX - canvasCenterX - ((windowCenterX - sourceCenterX) * scale),
            cropCenterY - canvasCenterY - ((windowCenterY - sourceCenterY) * scale),
            scale,
            0);
    }
}

public enum CanvasInteractionTool
{
    Crop,
    Transform,
}
