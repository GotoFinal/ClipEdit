using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.Controls;

/// <summary>
/// A lightweight source-time editor. Drag to choose a removal range; click or use
/// the arrow keys to move the source playhead.
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

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#252936"));
    private static readonly IBrush KeptBrush = new SolidColorBrush(Color.Parse("#5B45BE"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255));
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.Parse("#D8CCFF")), 1);
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.Parse("#F4F5FA")), 2);

    private double? _dragAnchor;

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
        _dragAnchor = XToTime(eventArgs.GetPosition(this).X);
        SetCurrentValue(SelectionStartProperty, _dragAnchor.Value);
        SetCurrentValue(SelectionEndProperty, _dragAnchor.Value);
        SetCurrentValue(PlayheadProperty, _dragAnchor.Value);
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (_dragAnchor is null || eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var current = XToTime(eventArgs.GetPosition(this).X);
        SetCurrentValue(SelectionStartProperty, Math.Min(_dragAnchor.Value, current));
        SetCurrentValue(SelectionEndProperty, Math.Max(_dragAnchor.Value, current));
        SetCurrentValue(PlayheadProperty, current);
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
        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        var step = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1d : 1d / 30;
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

    private double TimeToX(double seconds)
    {
        return Math.Clamp(seconds / Duration, 0, 1) * Bounds.Width;
    }

    private double XToTime(double x)
    {
        return Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Duration;
    }
}
