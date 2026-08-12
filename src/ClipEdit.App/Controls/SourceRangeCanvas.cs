using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

    private static readonly IBrush TrackBrush = new ImmutableSolidColorBrush(0xFF252936);
    private static readonly IBrush KeptBrush = new ImmutableSolidColorBrush(0xFF5B45BE);
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

    static SourceRangeCanvas()
    {
        AffectsRender<SourceRangeCanvas>(
            KeptRangesProperty,
            DurationProperty,
            PlayheadProperty,
            SelectionStartProperty,
            SelectionEndProperty);
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var track = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(TrackBrush, track, 6);
        if (Duration <= 0 || Bounds.Width <= 0)
        {
            return;
        }

        foreach (var range in KeptRanges ?? [])
        {
            var left = TimeToX(range.Start.TotalSeconds);
            var right = TimeToX(range.End.TotalSeconds);
            context.FillRectangle(KeptBrush, new Rect(left, 0, Math.Max(0, right - left), Bounds.Height), 5);
        }

        var selectionLeft = TimeToX(Math.Min(SelectionStart, SelectionEnd));
        var selectionRight = TimeToX(Math.Max(SelectionStart, SelectionEnd));
        if (selectionRight > selectionLeft)
        {
            var selection = new Rect(selectionLeft, 1, selectionRight - selectionLeft, Math.Max(0, Bounds.Height - 2));
            context.FillRectangle(SelectionBrush, selection, 4);
            context.DrawRectangle(null, SelectionPen, selection, 4);
            context.FillRectangle(
                EdgeHandleBrush,
                new Rect(selectionLeft - (EdgeHandleWidth / 2), 0, EdgeHandleWidth, Bounds.Height),
                2);
            context.FillRectangle(
                EdgeHandleBrush,
                new Rect(selectionRight - (EdgeHandleWidth / 2), 0, EdgeHandleWidth, Bounds.Height),
                2);
        }

        var playheadX = TimeToX(Playhead);
        context.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Duration <= 0)
        {
            return;
        }

        Focus();
        var pointerX = eventArgs.GetPosition(this).X;
        var normalizedStart = Math.Min(SelectionStart, SelectionEnd);
        var normalizedEnd = Math.Max(SelectionStart, SelectionEnd);
        _dragMode = GetDragMode(
            pointerX,
            TimeToX(normalizedStart),
            TimeToX(normalizedEnd));
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
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _dragAnchor = null;
        _dragMode = SourceRangeDragMode.None;
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
        return Math.Clamp(seconds / Duration, 0, 1) * Bounds.Width;
    }

    private double XToTime(double x)
    {
        return Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Duration;
    }
}

internal enum SourceRangeDragMode
{
    None,
    NewSelection,
    StartEdge,
    EndEdge,
}
