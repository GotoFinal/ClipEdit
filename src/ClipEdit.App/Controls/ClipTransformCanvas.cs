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

    public static readonly StyledProperty<double> WheelZoomPercentProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, double>(nameof(WheelZoomPercent), 10);

    public static readonly StyledProperty<int> WheelRotationDegreesProperty =
        AvaloniaProperty.Register<ClipTransformCanvas, int>(nameof(WheelRotationDegrees), 1);

    private static readonly IPen OutlinePen = new Pen(0xFF64D7F5, 1.5).ToImmutable();
    private static readonly IPen CenterPen = new Pen(0xCC64D7F5, 1).ToImmutable();
    private static readonly IBrush HandleBrush = new ImmutableSolidColorBrush(0xFF64D7F5);
    private static readonly IBrush RotationHandleBrush = new ImmutableSolidColorBrush(0xFFFFB454);
    private const double HandleRadius = 5;
    private const double HitRadius = 14;
    private const double RotationHandleDistance = 28;
    private Point _dragStartCanvas;
    private ClipCanvasTransform _dragStartTransform;
    private ClipTransformDragMode _dragMode;
    private bool _isTransformEditActive;

    public event EventHandler? TransformEditStarted;

    public event EventHandler? TransformEditCompleted;

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
        if (!IsActive)
        {
            return;
        }

        var viewport = GetCanvasViewport();
        var cornersCanvas = GetTransformedCorners(SourceSize, CanvasSize, Transform);
        var corners = cornersCanvas
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

        var handles = GetResizeHandles(corners);
        foreach (var handle in handles)
        {
            context.DrawRectangle(
                HandleBrush,
                null,
                new Rect(handle.Point.X - HandleRadius, handle.Point.Y - HandleRadius, HandleRadius * 2, HandleRadius * 2));
        }

        var rotationHandle = GetRotationHandle(corners, center);
        context.DrawLine(CenterPen, handles[1].Point, rotationHandle);
        context.DrawEllipse(RotationHandleBrush, null, rotationHandle, HandleRadius, HandleRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!IsActive || !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var viewport = GetCanvasViewport();
        var pointer = eventArgs.GetPosition(this);
        var corners = GetTransformedCorners(SourceSize, CanvasSize, Transform)
            .Select(point => CanvasToView(point, viewport))
            .ToArray();
        var center = CanvasToView(GetTransformCenter(CanvasSize, Transform), viewport);
        _dragMode = GetDragMode(pointer, corners, center);
        if (_dragMode == ClipTransformDragMode.None)
        {
            return;
        }

        _dragStartCanvas = ViewToCanvas(pointer, viewport);
        _dragStartTransform = Transform;
        eventArgs.Pointer.Capture(this);
        BeginTransformEdit();
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (_dragMode == ClipTransformDragMode.None || eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var current = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
        var next = _dragMode switch
        {
            ClipTransformDragMode.Move => ApplyDrag(
                _dragStartTransform,
                current.X - _dragStartCanvas.X,
                current.Y - _dragStartCanvas.Y),
            ClipTransformDragMode.Rotate => ApplyRotation(
                _dragStartTransform,
                GetTransformCenter(CanvasSize, _dragStartTransform),
                _dragStartCanvas,
                current),
            _ => ApplyResize(
                _dragStartTransform,
                SourceSize,
                _dragMode,
                current.X - _dragStartCanvas.X,
                current.Y - _dragStartCanvas.Y,
                preserveAspectRatio: !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)),
        };
        SetCurrentValue(
            TransformProperty,
            next);
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        _dragMode = ClipTransformDragMode.None;
        EndTransformEdit();
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _dragMode = ClipTransformDragMode.None;
        EndTransformEdit();
    }

    private void BeginTransformEdit()
    {
        if (_isTransformEditActive)
        {
            return;
        }

        _isTransformEditActive = true;
        TransformEditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndTransformEdit()
    {
        if (!_isTransformEditActive)
        {
            return;
        }

        _isTransformEditActive = false;
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
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
            var rotationDelta = CalculateWheelRotationDelta(eventArgs.Delta.Y, WheelRotationDegrees);
            SetCurrentValue(TransformProperty, Transform.Rotate(Transform.RotationDegrees + rotationDelta));
        }
        else
        {
            var pointer = ViewToCanvas(eventArgs.GetPosition(this), GetCanvasViewport());
            SetCurrentValue(
                TransformProperty,
                ApplyZoomAt(Transform, pointer, CanvasSize, CalculateWheelZoomFactor(eventArgs.Delta.Y, WheelZoomPercent)));
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
        var minimumFactor = Math.Max(0.01 / start.ScaleX, 0.01 / start.ScaleY);
        var maximumFactor = Math.Min(100 / start.ScaleX, 100 / start.ScaleY);
        var actualFactor = Math.Clamp(factor, minimumFactor, maximumFactor);
        var relativeX = pointerCanvas.X - (canvasSize.Width / 2d);
        var relativeY = pointerCanvas.Y - (canvasSize.Height / 2d);
        return new ClipCanvasTransform(
            relativeX - ((relativeX - start.OffsetX) * actualFactor),
            relativeY - ((relativeY - start.OffsetY) * actualFactor),
            start.ScaleX * actualFactor,
            start.ScaleY * actualFactor,
            start.RotationDegrees,
            start.IsHorizontallyMirrored,
            start.IsVerticallyMirrored);
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


    internal static double CalculateWheelZoomFactor(double wheelDelta, double zoomPercent)
    {
        var boundedPercent = double.IsFinite(zoomPercent)
            ? Math.Clamp(zoomPercent, 1, 50)
            : 10;
        return Math.Pow(1 + (boundedPercent / 100), wheelDelta);
    }

    internal static int CalculateWheelRotationDelta(double wheelDelta, int rotationDegrees)
    {
        return checked((int)Math.Round(
            wheelDelta * Math.Clamp(rotationDegrees, 1, 45),
            MidpointRounding.AwayFromZero));
    }
    private Point ViewToCanvas(Point point, Rect viewport) =>
        new(
            (point.X - viewport.X) * CanvasSize.Width / viewport.Width,
            (point.Y - viewport.Y) * CanvasSize.Height / viewport.Height);

    private Point CanvasToView(Point point, Rect viewport) =>
        new(
            viewport.X + (point.X * viewport.Width / CanvasSize.Width),
            viewport.Y + (point.Y * viewport.Height / CanvasSize.Height));

    internal static IReadOnlyList<Point> GetTransformedCorners(
        DomainPixelSize sourceSize,
        DomainPixelSize canvasSize,
        ClipCanvasTransform transform)
    {
        var radians = transform.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centerX = (canvasSize.Width / 2d) + transform.OffsetX;
        var centerY = (canvasSize.Height / 2d) + transform.OffsetY;
        var halfWidth = sourceSize.Width / 2d;
        var halfHeight = sourceSize.Height / 2d;
        return new[]
        {
            TransformPoint(-halfWidth, -halfHeight),
            TransformPoint(halfWidth, -halfHeight),
            TransformPoint(halfWidth, halfHeight),
            TransformPoint(-halfWidth, halfHeight),
        };

        Point TransformPoint(double x, double y)
        {
            var rotatedX = (x * cosine) - (y * sine);
            var rotatedY = (x * sine) + (y * cosine);
            return new Point(
                centerX + (rotatedX * transform.ScaleX),
                centerY + (rotatedY * transform.ScaleY));
        }
    }

    internal static ClipCanvasTransform ApplyResize(
        ClipCanvasTransform start,
        DomainPixelSize sourceSize,
        ClipTransformDragMode mode,
        double deltaX,
        double deltaY,
        bool preserveAspectRatio)
    {
        var radians = start.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var horizontalSign = mode.HasFlag(ClipTransformDragMode.Left) ? -1 : mode.HasFlag(ClipTransformDragMode.Right) ? 1 : 0;
        var verticalSign = mode.HasFlag(ClipTransformDragMode.Top) ? -1 : mode.HasFlag(ClipTransformDragMode.Bottom) ? 1 : 0;
        var localX = horizontalSign * sourceSize.Width / 2d;
        var localY = verticalSign * sourceSize.Height / 2d;
        var rotatedX = (localX * cosine) - (localY * sine);
        var rotatedY = (localX * sine) + (localY * cosine);
        var startHalfVectorX = rotatedX * start.ScaleX;
        var startHalfVectorY = rotatedY * start.ScaleY;
        double scaleX;
        double scaleY;
        if (preserveAspectRatio)
        {
            var vectorLengthSquared =
                (startHalfVectorX * startHalfVectorX) +
                (startHalfVectorY * startHalfVectorY);
            var factor = vectorLengthSquared <= 0.000_001
                ? 1
                : ((startHalfVectorX * (startHalfVectorX + (deltaX / 2))) +
                   (startHalfVectorY * (startHalfVectorY + (deltaY / 2)))) /
                  vectorLengthSquared;
            factor = Math.Clamp(
                factor,
                Math.Max(0.01 / start.ScaleX, 0.01 / start.ScaleY),
                Math.Min(100 / start.ScaleX, 100 / start.ScaleY));
            scaleX = start.ScaleX * factor;
            scaleY = start.ScaleY * factor;
        }
        else
        {
            scaleX = Math.Abs(rotatedX) <= 0.000_001
                ? start.ScaleX
                : Math.Clamp(
                    (startHalfVectorX + (deltaX / 2)) / rotatedX,
                    0.01,
                    100);
            scaleY = Math.Abs(rotatedY) <= 0.000_001
                ? start.ScaleY
                : Math.Clamp(
                    (startHalfVectorY + (deltaY / 2)) / rotatedY,
                    0.01,
                    100);
        }

        var centerShiftX = (rotatedX * scaleX) - startHalfVectorX;
        var centerShiftY = (rotatedY * scaleY) - startHalfVectorY;
        return new ClipCanvasTransform(
            start.OffsetX + centerShiftX,
            start.OffsetY + centerShiftY,
            scaleX,
            scaleY,
            start.RotationDegrees,
            start.IsHorizontallyMirrored,
            start.IsVerticallyMirrored);
    }

    internal static ClipCanvasTransform ApplyRotation(
        ClipCanvasTransform start,
        Point center,
        Point startPointer,
        Point currentPointer)
    {
        var startAngle = Math.Atan2(startPointer.Y - center.Y, startPointer.X - center.X);
        var currentAngle = Math.Atan2(currentPointer.Y - center.Y, currentPointer.X - center.X);
        var deltaDegrees = checked((int)Math.Round((currentAngle - startAngle) * 180 / Math.PI));
        return start.Rotate(start.RotationDegrees + deltaDegrees);
    }

    internal static ClipTransformDragMode GetDragMode(
        Point pointer,
        IReadOnlyList<Point> corners,
        Point center)
    {
        var rotationHandle = GetRotationHandle(corners, center);
        if (Distance(pointer, rotationHandle) <= HitRadius)
        {
            return ClipTransformDragMode.Rotate;
        }

        foreach (var handle in GetResizeHandles(corners))
        {
            if (Distance(pointer, handle.Point) <= HitRadius)
            {
                return handle.Mode;
            }
        }

        return ContainsPoint(pointer, corners)
            ? ClipTransformDragMode.Move
            : ClipTransformDragMode.None;
    }

    private static IReadOnlyList<(Point Point, ClipTransformDragMode Mode)> GetResizeHandles(IReadOnlyList<Point> corners)
    {
        return
        [
            (corners[0], ClipTransformDragMode.Left | ClipTransformDragMode.Top),
            (Midpoint(corners[0], corners[1]), ClipTransformDragMode.Top),
            (corners[1], ClipTransformDragMode.Right | ClipTransformDragMode.Top),
            (Midpoint(corners[1], corners[2]), ClipTransformDragMode.Right),
            (corners[2], ClipTransformDragMode.Right | ClipTransformDragMode.Bottom),
            (Midpoint(corners[2], corners[3]), ClipTransformDragMode.Bottom),
            (corners[3], ClipTransformDragMode.Left | ClipTransformDragMode.Bottom),
            (Midpoint(corners[3], corners[0]), ClipTransformDragMode.Left),
        ];
    }

    private static Point GetRotationHandle(IReadOnlyList<Point> corners, Point center)
    {
        var top = Midpoint(corners[0], corners[1]);
        var directionX = top.X - center.X;
        var directionY = top.Y - center.Y;
        var length = Math.Max(1, Math.Sqrt((directionX * directionX) + (directionY * directionY)));
        var inset = Math.Min(RotationHandleDistance, length / 2);
        return new Point(
            top.X - (directionX * inset / length),
            top.Y - (directionY * inset / length));
    }

    internal static Point GetTransformCenter(DomainPixelSize canvasSize, ClipCanvasTransform transform) =>
        new((canvasSize.Width / 2d) + transform.OffsetX, (canvasSize.Height / 2d) + transform.OffsetY);

    private static Point Midpoint(Point left, Point right) =>
        new((left.X + right.X) / 2, (left.Y + right.Y) / 2);

    private static double Distance(Point left, Point right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static bool ContainsPoint(Point point, IReadOnlyList<Point> polygon)
    {
        var inside = false;
        for (var current = 0; current < polygon.Count; current++)
        {
            var previous = (current + polygon.Count - 1) % polygon.Count;
            if ((polygon[current].Y > point.Y) != (polygon[previous].Y > point.Y) &&
                point.X < ((polygon[previous].X - polygon[current].X) *
                           (point.Y - polygon[current].Y) /
                           (polygon[previous].Y - polygon[current].Y)) + polygon[current].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

[Flags]
internal enum ClipTransformDragMode
{
    None = 0,
    Move = 1,
    Left = 2,
    Top = 4,
    Right = 8,
    Bottom = 16,
    Rotate = 32,
}
