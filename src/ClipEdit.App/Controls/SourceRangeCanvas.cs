using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.Controls;

/// <summary>
/// A lightweight source-time editor. Drag an empty area to make a selection, drag
/// either visible edge to trim it, or focus an edge and use arrows for fine adjustment.
/// </summary>
public sealed class SourceRangeCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<MediaRange>?> KeptRangesProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, IReadOnlyList<MediaRange>?>(nameof(KeptRanges));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(Duration));

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(Playhead));

    public static readonly StyledProperty<double> SelectionStartProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(SelectionStart));

    public static readonly StyledProperty<double> SelectionEndProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(SelectionEnd));

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(Step), 1d / 30);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(Zoom), 1d);

    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(ViewportStart));

    public static readonly StyledProperty<IReadOnlyList<TimelineThumbnailFrame>?> ThumbnailFramesProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, IReadOnlyList<TimelineThumbnailFrame>?>(nameof(ThumbnailFrames));

    public static readonly StyledProperty<TimelineBitmapVisual?> WaveformProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, TimelineBitmapVisual?>(nameof(Waveform));

    private static readonly IBrush TrackBrush = new ImmutableSolidColorBrush(0xFF252936);
    private static readonly IBrush KeptBrush = new ImmutableSolidColorBrush(0x665B45BE);
    private static readonly IBrush RemovedBrush = new ImmutableSolidColorBrush(0xB8151820);
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(0x41FFFFFF);
    private static readonly IPen SelectionPen = new Pen(0xFFD8CCFF, 1).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(0xFFF4F5FA, 2).ToImmutable();
    private static readonly IBrush EdgeHandleBrush = new ImmutableSolidColorBrush(0xFFF4F5FA);
    private const double EdgeHitWidth = 12;
    private const double EdgeHandleWidth = 4;

    private double? _dragAnchor;
    private double _dragStart;
    private double _dragEnd;
    private SourceRangeDragMode _dragMode;
    private SourceRangeDragMode _activeEdge;
    private bool _isPanning;
    private double _panStartX;
    private double _panViewportStart;

    static SourceRangeCanvas()
    {
        AffectsRender<SourceRangeCanvas>(
            KeptRangesProperty,
            DurationProperty,
            PlayheadProperty,
            SelectionStartProperty,
            SelectionEndProperty,
            ZoomProperty,
            ViewportStartProperty,
            ThumbnailFramesProperty,
            WaveformProperty);
    }

    public SourceRangeCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public IReadOnlyList<MediaRange>? KeptRanges
    {
        get => GetValue(KeptRangesProperty);
        set => SetValue(KeptRangesProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Playhead
    {
        get => GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public double SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public double SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double ViewportStart
    {
        get => GetValue(ViewportStartProperty);
        set => SetValue(ViewportStartProperty, value);
    }

    public IReadOnlyList<TimelineThumbnailFrame>? ThumbnailFrames
    {
        get => GetValue(ThumbnailFramesProperty);
        set => SetValue(ThumbnailFramesProperty, value);
    }

    public TimelineBitmapVisual? Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var track = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(TrackBrush, track, 6);
        if (Duration <= 0 || Bounds.Width <= 0)
        {
            return;
        }

        DrawAnalysisVisuals(context);

        foreach (var range in KeptRanges ?? [])
        {
            var start = Math.Max(range.Start.TotalSeconds, EffectiveViewportStart);
            var end = Math.Min(range.End.TotalSeconds, EffectiveViewportEnd);
            if (end <= start)
            {
                continue;
            }

            var left = TimeToX(start);
            var right = TimeToX(end);
            context.FillRectangle(KeptBrush, new Rect(left, 0, right - left, Bounds.Height), 5);
        }

        DrawRemovedRanges(context);

        var selectionLeft = TimeToX(Math.Min(SelectionStart, SelectionEnd));
        var selectionRight = TimeToX(Math.Max(SelectionStart, SelectionEnd));
        if (selectionRight > selectionLeft)
        {
            var selection = new Rect(selectionLeft, 1, selectionRight - selectionLeft, Math.Max(0, Bounds.Height - 2));
            context.FillRectangle(SelectionBrush, selection, 4);
            context.DrawRectangle(null, SelectionPen, selection, 4);
            if (IsVisibleTime(Math.Min(SelectionStart, SelectionEnd)))
            {
                context.FillRectangle(
                    EdgeHandleBrush,
                    new Rect(selectionLeft - (EdgeHandleWidth / 2), 0, EdgeHandleWidth, Bounds.Height),
                    2);
            }

            if (IsVisibleTime(Math.Max(SelectionStart, SelectionEnd)))
            {
                context.FillRectangle(
                    EdgeHandleBrush,
                    new Rect(selectionRight - (EdgeHandleWidth / 2), 0, EdgeHandleWidth, Bounds.Height),
                    2);
            }
        }

        if (IsVisibleTime(Playhead))
        {
            var playheadX = TimeToX(Playhead);
            context.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        var point = eventArgs.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed && Duration > 0 && EffectiveZoom > 1)
        {
            Focus();
            _isPanning = true;
            _panStartX = eventArgs.GetPosition(this).X;
            _panViewportStart = EffectiveViewportStart;
            eventArgs.Pointer.Capture(this);
            eventArgs.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || Duration <= 0)
        {
            return;
        }

        Focus();
        var pointerX = eventArgs.GetPosition(this).X;
        var normalizedStart = Math.Min(SelectionStart, SelectionEnd);
        var normalizedEnd = Math.Max(SelectionStart, SelectionEnd);
        _dragMode = GetDragMode(
            pointerX,
            IsVisibleTime(normalizedStart) ? TimeToX(normalizedStart) : double.NegativeInfinity,
            IsVisibleTime(normalizedEnd) ? TimeToX(normalizedEnd) : double.PositiveInfinity);
        _activeEdge = _dragMode is SourceRangeDragMode.StartEdge or SourceRangeDragMode.EndEdge
            ? _dragMode
            : SourceRangeDragMode.None;
        _dragAnchor = XToTime(pointerX);
        _dragStart = normalizedStart;
        _dragEnd = normalizedEnd;
        ApplySelection(ApplyDrag(_dragMode, _dragAnchor.Value, _dragStart, _dragEnd, _dragAnchor.Value));
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (_isPanning && eventArgs.Pointer.Captured == this)
        {
            var deltaX = eventArgs.GetPosition(this).X - _panStartX;
            SetCurrentValue(
                ViewportStartProperty,
                ClampViewportStart(_panViewportStart - (deltaX * EffectiveViewportDuration / Math.Max(1, Bounds.Width))));
            eventArgs.Handled = true;
            return;
        }

        if (_dragAnchor is null ||
            _dragMode == SourceRangeDragMode.None ||
            eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var current = XToTime(eventArgs.GetPosition(this).X);
        ApplySelection(ApplyDrag(_dragMode, _dragAnchor.Value, _dragStart, _dragEnd, current));
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }

        _dragAnchor = null;
        _dragMode = SourceRangeDragMode.None;
        _isPanning = false;
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _dragAnchor = null;
        _dragMode = SourceRangeDragMode.None;
        _isPanning = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        if (Duration <= 0 || eventArgs.Delta.Y == 0)
        {
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) && EffectiveZoom > 1)
        {
            SetCurrentValue(
                ViewportStartProperty,
                ClampViewportStart(EffectiveViewportStart - (eventArgs.Delta.Y * EffectiveViewportDuration * 0.12)));
        }
        else
        {
            var pointerX = eventArgs.GetPosition(this).X;
            var anchor = XToTime(pointerX);
            var requestedZoom = Math.Clamp(
                EffectiveZoom * Math.Pow(1.25, eventArgs.Delta.Y),
                1,
                TimelineViewportMath.MaximumZoom);
            var relative = Math.Clamp(pointerX / Math.Max(1, Bounds.Width), 0, 1);
            var newDuration = Duration / requestedZoom;
            SetCurrentValue(ZoomProperty, requestedZoom);
            SetCurrentValue(ViewportStartProperty, ClampViewportStart(anchor - (relative * newDuration), requestedZoom));
        }

        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        var step = Math.Max(0.000001, Step) *
                   (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
        if (_activeEdge is SourceRangeDragMode.StartEdge or SourceRangeDragMode.EndEdge &&
            eventArgs.Key is Key.Left or Key.Right)
        {
            var direction = eventArgs.Key == Key.Left ? -1 : 1;
            var current = _activeEdge == SourceRangeDragMode.StartEdge
                ? SelectionStart
                : SelectionEnd;
            ApplySelection(ApplyDrag(
                _activeEdge,
                current,
                Math.Min(SelectionStart, SelectionEnd),
                Math.Max(SelectionStart, SelectionEnd),
                current + (direction * step)));
            eventArgs.Handled = true;
            return;
        }

        var next = eventArgs.Key switch
        {
            Key.Left => Math.Max(0, Playhead - step),
            Key.Right => Math.Min(Duration, Playhead + step),
            Key.Home => 0,
            Key.End => Duration,
            _ => Playhead,
        };

        if (next != Playhead)
        {
            SetCurrentValue(PlayheadProperty, next);
            eventArgs.Handled = true;
        }
    }

    internal static (double Start, double End) NormalizeSelection(double anchor, double current)
    {
        return (Math.Min(anchor, current), Math.Max(anchor, current));
    }

    internal static SourceRangeDragMode GetDragMode(
        double pointerX,
        double selectionStartX,
        double selectionEndX)
    {
        var startDistance = Math.Abs(pointerX - selectionStartX);
        var endDistance = Math.Abs(pointerX - selectionEndX);
        if (Math.Min(startDistance, endDistance) <= EdgeHitWidth)
        {
            return startDistance <= endDistance
                ? SourceRangeDragMode.StartEdge
                : SourceRangeDragMode.EndEdge;
        }

        return SourceRangeDragMode.NewSelection;
    }

    internal static (double Start, double End, double Playhead) ApplyDrag(
        SourceRangeDragMode mode,
        double anchor,
        double selectionStart,
        double selectionEnd,
        double current)
    {
        return mode switch
        {
            SourceRangeDragMode.StartEdge =>
                (Math.Min(current, selectionEnd), selectionEnd, Math.Min(current, selectionEnd)),
            SourceRangeDragMode.EndEdge =>
                (selectionStart, Math.Max(current, selectionStart), Math.Max(current, selectionStart)),
            SourceRangeDragMode.NewSelection =>
                (Math.Min(anchor, current), Math.Max(anchor, current), current),
            _ => (selectionStart, selectionEnd, current),
        };
    }

    private void ApplySelection((double Start, double End, double Playhead) selection)
    {
        var start = Math.Clamp(selection.Start, 0, Duration);
        var end = Math.Clamp(selection.End, start, Duration);
        SetCurrentValue(SelectionStartProperty, start);
        SetCurrentValue(SelectionEndProperty, end);
        SetCurrentValue(PlayheadProperty, Math.Clamp(selection.Playhead, start, end));
    }

    private double TimeToX(double seconds)
    {
        return ((seconds - EffectiveViewportStart) / EffectiveViewportDuration) * Bounds.Width;
    }

    private double XToTime(double x)
    {
        return EffectiveViewportStart +
               (Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * EffectiveViewportDuration);
    }

    private double EffectiveZoom => Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1, 1, TimelineViewportMath.MaximumZoom);

    private double EffectiveViewportDuration => Duration / EffectiveZoom;

    private double EffectiveViewportStart => ClampViewportStart(ViewportStart);

    private double EffectiveViewportEnd => EffectiveViewportStart + EffectiveViewportDuration;

    private bool IsVisibleTime(double seconds) =>
        seconds >= EffectiveViewportStart && seconds <= EffectiveViewportEnd;

    private double ClampViewportStart(double start, double? zoom = null)
    {
        var effectiveZoom = Math.Clamp(zoom ?? EffectiveZoom, 1, TimelineViewportMath.MaximumZoom);
        return Math.Clamp(
            double.IsFinite(start) ? start : 0,
            0,
            Math.Max(0, Duration - (Duration / effectiveZoom)));
    }

    private void DrawAnalysisVisuals(DrawingContext context)
    {
        using var clip = context.PushClip(new Rect(Bounds.Size));
        if (Waveform is { } waveform)
        {
            DrawBitmapRange(context, waveform.Image, waveform.Start, waveform.End);
        }

        foreach (var thumbnail in ThumbnailFrames ?? [])
        {
            var start = Math.Max(thumbnail.Start, EffectiveViewportStart);
            var end = Math.Min(thumbnail.End, EffectiveViewportEnd);
            if (end <= start)
            {
                continue;
            }

            var destination = new Rect(
                TimeToX(start),
                0,
                TimeToX(end) - TimeToX(start),
                Bounds.Height);
            var source = CreateCoverSourceRect(
                thumbnail.Image.PixelSize.Width,
                thumbnail.Image.PixelSize.Height,
                destination.Width,
                destination.Height);
            context.DrawImage(thumbnail.Image, source, destination);
        }
    }

    private void DrawBitmapRange(DrawingContext context, Avalonia.Media.Imaging.Bitmap image, double start, double end)
    {
        var visibleStart = Math.Max(start, EffectiveViewportStart);
        var visibleEnd = Math.Min(end, EffectiveViewportEnd);
        if (visibleEnd <= visibleStart)
        {
            return;
        }

        var imageWidth = image.PixelSize.Width;
        var sourceLeft = ((visibleStart - start) / (end - start)) * imageWidth;
        var sourceRight = ((visibleEnd - start) / (end - start)) * imageWidth;
        context.DrawImage(
            image,
            new Rect(sourceLeft, 0, sourceRight - sourceLeft, image.PixelSize.Height),
            new Rect(
                TimeToX(visibleStart),
                0,
                TimeToX(visibleEnd) - TimeToX(visibleStart),
                Bounds.Height));
    }

    private void DrawRemovedRanges(DrawingContext context)
    {
        var cursor = EffectiveViewportStart;
        foreach (var range in (KeptRanges ?? []).OrderBy(range => range.Start))
        {
            var keptStart = Math.Clamp(range.Start.TotalSeconds, EffectiveViewportStart, EffectiveViewportEnd);
            var keptEnd = Math.Clamp(range.End.TotalSeconds, EffectiveViewportStart, EffectiveViewportEnd);
            if (keptStart > cursor)
            {
                DrawRemovedRange(context, cursor, keptStart);
            }

            cursor = Math.Max(cursor, keptEnd);
        }

        if (cursor < EffectiveViewportEnd)
        {
            DrawRemovedRange(context, cursor, EffectiveViewportEnd);
        }
    }

    private void DrawRemovedRange(DrawingContext context, double start, double end)
    {
        context.FillRectangle(
            RemovedBrush,
            new Rect(TimeToX(start), 0, Math.Max(0, TimeToX(end) - TimeToX(start)), Bounds.Height));
    }

    private static Rect CreateCoverSourceRect(
        double sourceWidth,
        double sourceHeight,
        double destinationWidth,
        double destinationHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return new Rect(0, 0, Math.Max(0, sourceWidth), Math.Max(0, sourceHeight));
        }

        var sourceAspect = sourceWidth / sourceHeight;
        var destinationAspect = destinationWidth / destinationHeight;
        if (sourceAspect > destinationAspect)
        {
            var width = sourceHeight * destinationAspect;
            return new Rect((sourceWidth - width) / 2, 0, width, sourceHeight);
        }

        var height = sourceWidth / destinationAspect;
        return new Rect(0, (sourceHeight - height) / 2, sourceWidth, height);
    }
}

internal enum SourceRangeDragMode
{
    None,
    NewSelection,
    StartEdge,
    EndEdge,
}
