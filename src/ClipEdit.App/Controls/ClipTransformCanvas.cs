using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClipEdit.Domain.Geometry;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Controls;

/// <summary>
/// Transparent direct-manipulation layer for moving, scaling, and rotating the
/// selected clip beneath the shared crop overlay. It never renders video.
/// </summary>
public sealed class ClipTransformCanvas : Control
{
    public static readonly StyledProperty<DomainPixelSize> SourceSizeProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, DomainPixelSize>(
            nameof(SourceSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<DomainPixelSize> CanvasSizeProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, DomainPixelSize>(
            nameof(CanvasSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<ClipCanvasTransform> TransformProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, ClipCanvasTransform>(
            nameof(Transform),
            ClipCanvasTransform.Identity);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, bool>(nameof(IsActive));

    private static readonly IPen OutlinePen = new Pen(0xFF64D7F5, 1.5).ToImmutable();
    private static readonly IPen CenterPen = new Pen(0xCC64D7F5, 1).ToImmutable();
    private Point _dragStartCanvas;
    private ClipCanvasTransform _dragStartTransform;
    private bool _isDragging;

    static ClipTransformCanvas()
    {
        AffectsRender<ClipTransformCanvas>(
            SourceSizeProperty,
            CanvasSizeProperty,
            TransformProperty,
            IsActiveProperty);
    }

    public ClipTransformCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
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

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!IsActive)
        {
            return;
        }

        var viewport = GetCanvasViewport();
        var corners = GetTransformedCorners(SourceSize, CanvasSize, Transform)
            .Select(point => CanvasToView(point, viewport))
            .ToArray();
        for (var index = 0; index < corners.Length; index++)
        {
            context.DrawLine(OutlinePen, corners[index], corners[(index + 1) % corners.Length]);
        }

        var center = CanvasToView(
            new Point(
                (CanvasSize.Width / 2d) + Transform.OffsetX,
                (CanvasSize.Height / 2d) + Transform.OffsetY),
            viewport);
        context.DrawLine(
            CenterPen,
            new Point(center.X - 8, center.Y),
            new Point(center.X + 8, center.Y));
        context.DrawLine(
            CenterPen,
            new Point(center.X, center.Y - 8),
            new Point(center.X, center.Y + 8));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!IsActive || !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _dragStartCanvas = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
        _dragStartTransform = Transform;
        _isDragging = true;
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (!_isDragging || eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var current = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
        SetCurrentValue(
            TransformProperty,
            ApplyDrag(
                _dragStartTransform,
                current.X - _dragStartCanvas.X,
                current.Y - _dragStartCanvas.Y));
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        _isDragging = false;
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _isDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        if (!IsActive || eventArgs.Delta.Y == 0)
        {
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var rotationDelta = checked((int)Math.Round(eventArgs.Delta.Y * 5));
            SetCurrentValue(TransformProperty, Transform.Rotate(Transform.RotationDegrees + rotationDelta));
        }
        else
        {
            var pointer = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
            SetCurrentValue(
                TransformProperty,
                ApplyZoomAt(Transform, pointer, CanvasSize, Math.Pow(1.1, eventArgs.Delta.Y)));
        }

        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (!IsActive)
        {
            return;
        }

        var step = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var next = eventArgs.Key switch
        {
            Key.Left => ApplyDrag(Transform, -step, 0),
            Key.Right => ApplyDrag(Transform, step, 0),
            Key.Up => ApplyDrag(Transform, 0, -step),
            Key.Down => ApplyDrag(Transform, 0, step),
            Key.Q => Transform.Rotate(Transform.RotationDegrees - step),
            Key.E => Transform.Rotate(Transform.RotationDegrees + step),
            _ => Transform,
        };
        if (next != Transform)
        {
            SetCurrentValue(TransformProperty, next);
            eventArgs.Handled = true;
        }
    }

    internal static ClipCanvasTransform ApplyDrag(
        ClipCanvasTransform start,
        double deltaX,
        double deltaY) =>
        start.MoveTo(start.OffsetX + deltaX, start.OffsetY + deltaY);

    internal static ClipCanvasTransform ApplyZoomAt(
        ClipCanvasTransform start,
        Point pointerCanvas,
        DomainPixelSize canvasSize,
        double factor)
    {
        var newScale = Math.Clamp(start.Scale * factor, 0.01, 100);
        var actualFactor = newScale / start.Scale;
        var relativeX = pointerCanvas.X - (canvasSize.Width / 2d);
        var relativeY = pointerCanvas.Y - (canvasSize.Height / 2d);
        return new ClipCanvasTransform(
            relativeX - ((relativeX - start.OffsetX) * actualFactor),
            relativeY - ((relativeY - start.OffsetY) * actualFactor),
            newScale,
            start.RotationDegrees);
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

    private Point ViewToCanvas(Point point, Rect viewport) =>
        new(
            (point.X - viewport.X) * CanvasSize.Width / viewport.Width,
            (point.Y - viewport.Y) * CanvasSize.Height / viewport.Height);

    private Point CanvasToView(Point point, Rect viewport) =>
        new(
            viewport.X + (point.X * viewport.Width / CanvasSize.Width),
            viewport.Y + (point.Y * viewport.Height / CanvasSize.Height));

    private static IReadOnlyList<Point> GetTransformedCorners(
        DomainPixelSize sourceSize,
        DomainPixelSize canvasSize,
        ClipCanvasTransform transform)
    {
        var radians = transform.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centerX = (canvasSize.Width / 2d) + transform.OffsetX;
        var centerY = (canvasSize.Height / 2d) + transform.OffsetY;
        var halfWidth = sourceSize.Width * transform.Scale / 2;
        var halfHeight = sourceSize.Height * transform.Scale / 2;
        return new[]
        {
            TransformPoint(-halfWidth, -halfHeight),
            TransformPoint(halfWidth, -halfHeight),
            TransformPoint(halfWidth, halfHeight),
            TransformPoint(-halfWidth, halfHeight),
        };

        Point TransformPoint(double x, double y) =>
            new(
                centerX + (x * cosine) - (y * sine),
                centerY + (x * sine) + (y * cosine));
    }
}
