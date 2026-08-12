using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipEdit.Domain.Geometry;
using DomainPixelSize = ClipEdit.Domain.Geometry.PixelSize;

namespace ClipEdit.App.Controls;

public sealed class CropCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CropCanvas, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<DomainPixelSize> SourceSizeProperty =
        AvaloniaProperty.Register<CropCanvas, DomainPixelSize>(
            nameof(SourceSize),
            new DomainPixelSize(1, 1));

    public static readonly StyledProperty<CropRegion> CropProperty =
        AvaloniaProperty.Register<CropCanvas, CropRegion>(nameof(Crop));

    public static readonly StyledProperty<bool> IsOverlayOnlyProperty =
        AvaloniaProperty.Register<CropCanvas, bool>(nameof(IsOverlayOnly));

    private const double HandleRadius = 6;
    private const double HitRadius = 12;
    private static readonly IBrush OutsideBrush = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private static readonly IPen CropPen = new Pen(new SolidColorBrush(Color.Parse("#F4F5FA")), 1.5);
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#F4F5FA"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), 1);

    private CropDragMode _dragMode;
    private Point _dragStartSource;
    private CropRegion _dragStartCrop;

    static CropCanvas()
    {
        AffectsRender<CropCanvas>(SourceProperty, SourceSizeProperty, CropProperty, IsOverlayOnlyProperty);
    }

    public CropCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public DomainPixelSize SourceSize
    {
        get => GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    public CropRegion Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public bool IsOverlayOnly
    {
        get => GetValue(IsOverlayOnlyProperty);
        set => SetValue(IsOverlayOnlyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!IsOverlayOnly)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        }

        var viewport = GetImageViewport();
        if (!IsOverlayOnly && Source is not null)
        {
            context.DrawImage(Source, viewport);
        }

        if (Crop.SourceSize != SourceSize)
        {
            return;
        }

        var cropRect = SourceToView(Crop, viewport);
        DrawOutsideMask(context, viewport, cropRect);
        DrawRuleOfThirds(context, cropRect);
        context.DrawRectangle(null, CropPen, cropRect);

        foreach (var handle in GetHandles(cropRect))
        {
            context.DrawRectangle(
                HandleBrush,
                null,
                new Rect(
                    handle.Point.X - HandleRadius,
                    handle.Point.Y - HandleRadius,
                    HandleRadius * 2,
                    HandleRadius * 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Crop.SourceSize != SourceSize)
        {
            return;
        }

        Focus();
        var viewport = GetImageViewport();
        var pointer = eventArgs.GetPosition(this);
        var cropRect = SourceToView(Crop, viewport);
        _dragMode = HitTest(pointer, cropRect);
        if (_dragMode == CropDragMode.None)
        {
            return;
        }

        _dragStartSource = ViewToSource(pointer, viewport);
        _dragStartCrop = Crop;
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (_dragMode == CropDragMode.None || eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var currentSource = ViewToSource(eventArgs.GetPosition(this), GetImageViewport());
        var deltaX = checked((int)Math.Round(currentSource.X - _dragStartSource.X));
        var deltaY = checked((int)Math.Round(currentSource.Y - _dragStartSource.Y));
        SetCurrentValue(CropProperty, ApplyDrag(_dragStartCrop, _dragMode, deltaX, deltaY));
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }

        _dragMode = CropDragMode.None;
        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        var step = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var moved = eventArgs.Key switch
        {
            Key.Left => Crop.MoveClamped(Crop.X - step, Crop.Y),
            Key.Right => Crop.MoveClamped(Crop.X + step, Crop.Y),
            Key.Up => Crop.MoveClamped(Crop.X, Crop.Y - step),
            Key.Down => Crop.MoveClamped(Crop.X, Crop.Y + step),
            _ => Crop,
        };

        if (moved != Crop)
        {
            SetCurrentValue(CropProperty, moved);
            eventArgs.Handled = true;
        }
    }

    internal static CropRegion ApplyDrag(
        CropRegion start,
        CropDragMode mode,
        int deltaX,
        int deltaY)
    {
        if (mode == CropDragMode.Move)
        {
            return start.MoveClamped(start.X + deltaX, start.Y + deltaY);
        }

        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        if (mode.HasFlag(CropDragMode.Left))
        {
            left = Math.Clamp(start.X + deltaX, 0, start.Right - 1);
        }

        if (mode.HasFlag(CropDragMode.Right))
        {
            right = Math.Clamp(start.Right + deltaX, start.X + 1, start.SourceSize.Width);
        }

        if (mode.HasFlag(CropDragMode.Top))
        {
            top = Math.Clamp(start.Y + deltaY, 0, start.Bottom - 1);
        }

        if (mode.HasFlag(CropDragMode.Bottom))
        {
            bottom = Math.Clamp(start.Bottom + deltaY, start.Y + 1, start.SourceSize.Height);
        }

        return CropRegion.FromEdges(start.SourceSize, left, top, right, bottom);
    }

    private Rect GetImageViewport()
    {
        var scale = Math.Min(Bounds.Width / SourceSize.Width, Bounds.Height / SourceSize.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return default;
        }

        var width = SourceSize.Width * scale;
        var height = SourceSize.Height * scale;
        return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    private Rect SourceToView(CropRegion crop, Rect viewport)
    {
        var scaleX = viewport.Width / SourceSize.Width;
        var scaleY = viewport.Height / SourceSize.Height;
        return new Rect(
            viewport.X + (crop.X * scaleX),
            viewport.Y + (crop.Y * scaleY),
            crop.Width * scaleX,
            crop.Height * scaleY);
    }

    private Point ViewToSource(Point point, Rect viewport)
    {
        return new Point(
            (point.X - viewport.X) * SourceSize.Width / viewport.Width,
            (point.Y - viewport.Y) * SourceSize.Height / viewport.Height);
    }

    private static void DrawOutsideMask(DrawingContext context, Rect viewport, Rect crop)
    {
        context.FillRectangle(OutsideBrush, new Rect(viewport.X, viewport.Y, viewport.Width, crop.Top - viewport.Top));
        context.FillRectangle(OutsideBrush, new Rect(viewport.X, crop.Bottom, viewport.Width, viewport.Bottom - crop.Bottom));
        context.FillRectangle(OutsideBrush, new Rect(viewport.X, crop.Top, crop.Left - viewport.Left, crop.Height));
        context.FillRectangle(OutsideBrush, new Rect(crop.Right, crop.Top, viewport.Right - crop.Right, crop.Height));
    }

    private static void DrawRuleOfThirds(DrawingContext context, Rect crop)
    {
        context.DrawLine(GridPen, new Point(crop.X + (crop.Width / 3), crop.Top), new Point(crop.X + (crop.Width / 3), crop.Bottom));
        context.DrawLine(GridPen, new Point(crop.X + (crop.Width * 2 / 3), crop.Top), new Point(crop.X + (crop.Width * 2 / 3), crop.Bottom));
        context.DrawLine(GridPen, new Point(crop.Left, crop.Y + (crop.Height / 3)), new Point(crop.Right, crop.Y + (crop.Height / 3)));
        context.DrawLine(GridPen, new Point(crop.Left, crop.Y + (crop.Height * 2 / 3)), new Point(crop.Right, crop.Y + (crop.Height * 2 / 3)));
    }

    private static CropDragMode HitTest(Point pointer, Rect crop)
    {
        foreach (var handle in GetHandles(crop))
        {
            if (Math.Abs(pointer.X - handle.Point.X) <= HitRadius &&
                Math.Abs(pointer.Y - handle.Point.Y) <= HitRadius)
            {
                return handle.Mode;
            }
        }

        return crop.Contains(pointer) ? CropDragMode.Move : CropDragMode.None;
    }

    private static IReadOnlyList<(Point Point, CropDragMode Mode)> GetHandles(Rect crop)
    {
        var centerX = crop.X + (crop.Width / 2);
        var centerY = crop.Y + (crop.Height / 2);
        return
        [
            (crop.TopLeft, CropDragMode.Left | CropDragMode.Top),
            (new Point(centerX, crop.Top), CropDragMode.Top),
            (crop.TopRight, CropDragMode.Right | CropDragMode.Top),
            (new Point(crop.Right, centerY), CropDragMode.Right),
            (crop.BottomRight, CropDragMode.Right | CropDragMode.Bottom),
            (new Point(centerX, crop.Bottom), CropDragMode.Bottom),
            (crop.BottomLeft, CropDragMode.Left | CropDragMode.Bottom),
            (new Point(crop.Left, centerY), CropDragMode.Left),
        ];
    }
}

[Flags]
internal enum CropDragMode
{
    None = 0,
    Move = 1,
    Left = 2,
    Top = 4,
    Right = 8,
    Bottom = 16,
}
