using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipEdit.Domain.Geometry;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Controls;

/// <summary>
/// Routes one preview surface between the shared crop and the selected clip
/// transform without rendering another video image.
/// </summary>
public sealed class AutoCanvas : Control
{
    public static readonly StyledProperty<DomainPixelSize> SourceSizeProperty =
        AvaloniaProperty.Register<AutoCanvas, DomainPixelSize>(
            nameof(SourceSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<DomainPixelSize> CanvasSizeProperty =
        AvaloniaProperty.Register<AutoCanvas, DomainPixelSize>(
            nameof(CanvasSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<ClipCanvasTransform> TransformProperty =
        AvaloniaProperty.Register<AutoCanvas, ClipCanvasTransform>(
            nameof(Transform),
            ClipCanvasTransform.Identity);

    public static readonly StyledProperty<CropRegion> CropProperty =
        AvaloniaProperty.Register<AutoCanvas, CropRegion>(
            nameof(Crop),
            CropRegion.FullFrame(new DomainPixelSize(1, 1)));

    public static readonly StyledProperty<int> CropSizeStepProperty =
        AvaloniaProperty.Register<AutoCanvas, int>(nameof(CropSizeStep), 1);

    public static readonly StyledProperty<bool> IsCropAspectRatioLockedProperty =
        AvaloniaProperty.Register<AutoCanvas, bool>(nameof(IsCropAspectRatioLocked));

    public static readonly StyledProperty<double> WheelZoomPercentProperty =
        AvaloniaProperty.Register<AutoCanvas, double>(nameof(WheelZoomPercent), 10);

    public static readonly StyledProperty<int> WheelRotationDegreesProperty =
        AvaloniaProperty.Register<AutoCanvas, int>(nameof(WheelRotationDegrees), 1);

    private CanvasAutoDragTarget _dragTarget;
    private ClipTransformDragMode _clipDragMode;
    private CropDragMode _cropDragMode;
    private Point _dragStartCanvas;
    private ClipCanvasTransform _dragStartTransform;
    private CropRegion _dragStartCrop;

    public AutoCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public DomainPixelSize SourceSize
    {
        get => GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    public DomainPixelSize CanvasSize
    {
        get => GetValue(CanvasSizeProperty);
        set => SetValue(CanvasSizeProperty, value);
    }

    public ClipCanvasTransform Transform
    {
        get => GetValue(TransformProperty);
        set => SetValue(TransformProperty, value);
    }

    public CropRegion Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public int CropSizeStep
    {
        get => GetValue(CropSizeStepProperty);
        set => SetValue(CropSizeStepProperty, value);
    }

    public bool IsCropAspectRatioLocked
    {
        get => GetValue(IsCropAspectRatioLockedProperty);
        set => SetValue(IsCropAspectRatioLockedProperty, value);
    }

    public double WheelZoomPercent
    {
        get => GetValue(WheelZoomPercentProperty);
        set => SetValue(WheelZoomPercentProperty, value);
    }

    public int WheelRotationDegrees
    {
        get => GetValue(WheelRotationDegreesProperty);
        set => SetValue(WheelRotationDegreesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            Crop.SourceSize != CanvasSize)
        {
            return;
        }

        var viewport = GetCanvasViewport();
        var pointerView = eventArgs.GetPosition(this);
        var clipCorners = ClipTransformCanvas
            .GetTransformedCorners(SourceSize, CanvasSize, Transform)
            .Select(point => CanvasToView(point, viewport))
            .ToArray();
        var clipCenter = CanvasToView(
            ClipTransformCanvas.GetTransformCenter(CanvasSize, Transform),
            viewport);
        _clipDragMode = ClipTransformCanvas.GetDragMode(pointerView, clipCorners, clipCenter);
        _cropDragMode = CropCanvas.GetDragMode(pointerView, GetCropViewport(Crop, viewport));
        var controlPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control);
        _dragTarget = ResolveDragTarget(_clipDragMode, _cropDragMode, controlPressed);
        if (_dragTarget == CanvasAutoDragTarget.None)
        {
            return;
        }

        if (_dragTarget == CanvasAutoDragTarget.Clip &&
            _clipDragMode == ClipTransformDragMode.None)
        {
            _clipDragMode = ClipTransformDragMode.Move;
        }

        Focus();
        _dragStartCanvas = ViewToCanvas(pointerView, viewport);
        _dragStartTransform = Transform;
        _dragStartCrop = Crop;
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (_dragTarget == CanvasAutoDragTarget.None || eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var current = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
        var deltaX = current.X - _dragStartCanvas.X;
        var deltaY = current.Y - _dragStartCanvas.Y;
        if (_dragTarget == CanvasAutoDragTarget.Crop)
        {
            var preserveAspect = IsCropAspectRatioLocked ||
                                 eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                                 eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control);
            SetCurrentValue(
                CropProperty,
                CropCanvas.ApplyDrag(
                    _dragStartCrop,
                    _cropDragMode,
                    checked((int)Math.Round(deltaX)),
                    checked((int)Math.Round(deltaY)),
                    preserveAspect,
                    CropSizeStep));
        }
        else
        {
            var next = _clipDragMode switch
            {
                ClipTransformDragMode.Move => ClipTransformCanvas.ApplyDrag(
                    _dragStartTransform,
                    deltaX,
                    deltaY),
                ClipTransformDragMode.Rotate => ClipTransformCanvas.ApplyRotation(
                    _dragStartTransform,
                    ClipTransformCanvas.GetTransformCenter(CanvasSize, _dragStartTransform),
                    _dragStartCanvas,
                    current),
                _ => ClipTransformCanvas.ApplyResize(
                    _dragStartTransform,
                    SourceSize,
                    _clipDragMode,
                    deltaX,
                    deltaY,
                    preserveAspectRatio: !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)),
            };
            SetCurrentValue(TransformProperty, next);
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        EndDrag(eventArgs.Pointer);
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        ClearDragState();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        if (eventArgs.Delta.Y == 0)
        {
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var delta = ClipTransformCanvas.CalculateWheelRotationDelta(
                eventArgs.Delta.Y,
                WheelRotationDegrees);
            SetCurrentValue(TransformProperty, Transform.Rotate(Transform.RotationDegrees + delta));
        }
        else
        {
            var pointer = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
            SetCurrentValue(
                TransformProperty,
                ClipTransformCanvas.ApplyZoomAt(
                    Transform,
                    pointer,
                    CanvasSize,
                    ClipTransformCanvas.CalculateWheelZoomFactor(eventArgs.Delta.Y, WheelZoomPercent)));
        }

        eventArgs.Handled = true;
    }

    internal static CanvasAutoDragTarget ResolveDragTarget(
        ClipTransformDragMode clipMode,
        CropDragMode cropMode,
        bool isControlPressed)
    {
        var isClipHandle = clipMode is not ClipTransformDragMode.None and not ClipTransformDragMode.Move;
        if (isClipHandle)
        {
            return CanvasAutoDragTarget.Clip;
        }

        if (cropMode == CropDragMode.Move && isControlPressed)
        {
            return CanvasAutoDragTarget.Clip;
        }

        return cropMode != CropDragMode.None
            ? CanvasAutoDragTarget.Crop
            : clipMode == ClipTransformDragMode.Move
                ? CanvasAutoDragTarget.Clip
                : CanvasAutoDragTarget.None;
    }

    private Rect GetCanvasViewport()
    {
        var scale = Math.Min(Bounds.Width / CanvasSize.Width, Bounds.Height / CanvasSize.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return new Rect(0, 0, 1, 1);
        }

        var width = CanvasSize.Width * scale;
        var height = CanvasSize.Height * scale;
        return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    private Rect GetCropViewport(CropRegion crop, Rect viewport) =>
        new(
            viewport.X + (crop.X * viewport.Width / CanvasSize.Width),
            viewport.Y + (crop.Y * viewport.Height / CanvasSize.Height),
            crop.Width * viewport.Width / CanvasSize.Width,
            crop.Height * viewport.Height / CanvasSize.Height);

    private Point ViewToCanvas(Point point, Rect viewport) =>
        new(
            (point.X - viewport.X) * CanvasSize.Width / viewport.Width,
            (point.Y - viewport.Y) * CanvasSize.Height / viewport.Height);

    private Point CanvasToView(Point point, Rect viewport) =>
        new(
            viewport.X + (point.X * viewport.Width / CanvasSize.Width),
            viewport.Y + (point.Y * viewport.Height / CanvasSize.Height));

    private void EndDrag(IPointer pointer)
    {
        if (pointer.Captured == this)
        {
            pointer.Capture(null);
        }

        ClearDragState();
    }

    private void ClearDragState()
    {
        _dragTarget = CanvasAutoDragTarget.None;
        _clipDragMode = ClipTransformDragMode.None;
        _cropDragMode = CropDragMode.None;
    }
}

internal enum CanvasAutoDragTarget
{
    None,
    Crop,
    Clip,
}
