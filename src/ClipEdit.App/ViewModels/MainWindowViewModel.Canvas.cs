using ClipEdit.App.Settings;
using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PixelSize _canvasSize = new(1, 1);
    private CropRegion _canvasCrop = CropRegion.FullFrame(new PixelSize(1, 1));
    private double _clipWheelZoomPercent = 10;
    private int _clipWheelRotationDegrees = 1;
    private int _clipboardExportMaximumMegabytes =
        CanvasInteractionSettings.DefaultClipboardExportMegabytes;
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

            MarkProjectDirty("canvas-crop");
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
    public int CropSizeStep => GetEffectiveExportPreset().RequiresEvenDimensions ? 2 : 1;


    public string CanvasSizeText => $"{CanvasSize.Width} × {CanvasSize.Height} canvas";
    public double ClipWheelZoomPercent
    {
        get => _clipWheelZoomPercent;
        set
        {
            var next = double.IsFinite(value) ? Math.Clamp(value, 1, 50) : 10;
            SetProperty(ref _clipWheelZoomPercent, next);
        }
    }

    public int ClipWheelRotationDegrees
    {
        get => _clipWheelRotationDegrees;
        set => SetProperty(ref _clipWheelRotationDegrees, Math.Clamp(value, 1, 45));
    }

    public int ClipboardExportMaximumMegabytes
    {
        get => _clipboardExportMaximumMegabytes;
        set => SetProperty(
            ref _clipboardExportMaximumMegabytes,
            Math.Clamp(
                value,
                CanvasInteractionSettings.MinimumClipboardExportMegabytes,
                CanvasInteractionSettings.MaximumClipboardExportMegabytes));
    }

    public long ClipboardExportMaximumBytes =>
        ClipboardExportMaximumMegabytes * 1_024L * 1_024L;

    public void ResetCanvasInteractionSettings()
    {
        ClipWheelZoomPercent = 10;
        ClipWheelRotationDegrees = 1;
        ClipboardExportMaximumMegabytes =
            CanvasInteractionSettings.DefaultClipboardExportMegabytes;
        StatusText = "Controls reset: wheel zoom 10%, rotation 1°, clipboard limit 100 MB";
    }

    public void ReportClipboardExportStatus(string message)
    {
        ReportStatus(message);
    }

    public void ReportStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        StatusText = message;
    }

    public CanvasInteractionTool CanvasTool
    {
        get => _canvasTool;
        set
        {
            if (SetProperty(ref _canvasTool, value))
            {
                OnPropertyChanged(nameof(IsCropToolActive));
                OnPropertyChanged(nameof(IsTransformToolActive));
                OnPropertyChanged(nameof(IsAutoToolActive));
                OnPropertyChanged(nameof(IsClipTransformOverlayActive));
                OnPropertyChanged(nameof(IsAutoCanvasOverlayActive));
                StatusText = value switch
                {
                    CanvasInteractionTool.Crop => "Crop tool: drag the shared frame or its handles",
                    CanvasInteractionTool.Transform => "Clip tool: drag the selected clip; wheel zooms; Shift+wheel rotates",
                    _ => "Auto tool: crop inside the frame; Ctrl drags the clip; clip handles stay active",
                };
            }
        }
    }

    public bool IsCropToolActive => CanvasTool == CanvasInteractionTool.Crop;

    public bool IsTransformToolActive => CanvasTool == CanvasInteractionTool.Transform;

    public bool IsAutoToolActive => CanvasTool == CanvasInteractionTool.Auto;

    public bool IsClipTransformOverlayActive =>
        !IsSequencePlayheadInGap && (IsTransformToolActive || IsAutoToolActive);
    public bool IsAutoCanvasOverlayActive => !IsSequencePlayheadInGap && IsAutoToolActive;

    public bool CanRotateCanvas => VideoClips.Count > 0;

    public void UseCropTool() => CanvasTool = CanvasInteractionTool.Crop;

    public void UseTransformTool() => CanvasTool = CanvasInteractionTool.Transform;

    public void UseAutoTool() => CanvasTool = CanvasInteractionTool.Auto;

    public bool RotateSelectedClipClockwise()
    {
        if (SelectedVideoClip is not { } clip)
        {
            return false;
        }

        clip.CanvasTransform = clip.CanvasTransform.Rotate(clip.CanvasTransform.RotationDegrees + 90);
        StatusText = $"Rotated {clip.DisplayName} 90° clockwise";
        return true;
    }

    public bool RotateCanvasClockwise()
    {
        if (!CanRotateCanvas)
        {
            return false;
        }

        var rotatedCrop = CanvasCrop.RotateSourceClockwise();
        var rotatedPreset = ResolveQuarterTurnCropPreset(SelectedCropAspectPreset);
        BeginClipTransformEdit(createDistinctHistoryEntry: true);
        try
        {
            CanvasSize = rotatedCrop.SourceSize;
            _canvasCrop = rotatedCrop;
            _selectedCropAspectPreset = rotatedPreset;
            RaiseCanvasCropChanged();
            OnPropertyChanged(nameof(SelectedCropAspectPreset));

            foreach (var clip in VideoClips)
            {
                clip.CanvasTransform = clip.CanvasTransform.RotateCanvasClockwise();
            }
        }
        finally
        {
            EndClipTransformEdit();
        }

        StatusText = $"Rotated the complete canvas 90° clockwise · {CanvasSize.Width} × {CanvasSize.Height}";
        return true;
    }

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
        OnPropertyChanged(nameof(IsAutoToolActive));
        OnPropertyChanged(nameof(IsClipTransformOverlayActive));
        OnPropertyChanged(nameof(IsAutoCanvasOverlayActive));
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
        return CenterCropWithSize(crop, width, height);
    }

    private CropRegion SnapCropPresetSizeCentered(CropRegion crop, CropAspectPreset preset)
    {
        if (preset.IsCustom || preset.IsFullFrame || CropSizeStep <= 1)
        {
            return SnapCropSizeCentered(crop);
        }

        var divisor = GreatestCommonDivisor(preset.WidthUnits, preset.HeightUnits);
        var unitWidth = preset.WidthUnits / divisor;
        var unitHeight = preset.HeightUnits / divisor;
        var scale = Math.Min(crop.Width / unitWidth, crop.Height / unitHeight);
        var scaleStep = LeastCommonMultiple(
            CropSizeStep / GreatestCommonDivisor(CropSizeStep, unitWidth),
            CropSizeStep / GreatestCommonDivisor(CropSizeStep, unitHeight));
        scale -= scale % scaleStep;
        if (scale <= 0)
        {
            return SnapCropSizeCentered(crop);
        }

        return CenterCropWithSize(crop, unitWidth * scale, unitHeight * scale);
    }

    private static CropRegion CenterCropWithSize(CropRegion crop, int width, int height)
    {
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

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }

    private static int LeastCommonMultiple(int left, int right)
    {
        return checked(left / GreatestCommonDivisor(left, right) * right);
    }

    private static CropAspectPreset ResolveQuarterTurnCropPreset(CropAspectPreset current)
    {
        if (current.IsCustom)
        {
            return BuiltInCropAspectPresets.Custom;
        }

        if (current.IsFullFrame)
        {
            return BuiltInCropAspectPresets.Source;
        }

        return BuiltInCropAspectPresets.All.FirstOrDefault(
                   preset =>
                       !preset.IsCustom &&
                       !preset.IsFullFrame &&
                       preset.WidthUnits == current.HeightUnits &&
                       preset.HeightUnits == current.WidthUnits) ??
               BuiltInCropAspectPresets.Custom;
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
    Auto,
}
