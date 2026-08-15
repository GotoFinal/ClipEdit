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
    public static readonly StyledProperty<IReadOnlyList<TimelineBitmapVisual>?> WaveformsProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, IReadOnlyList<TimelineBitmapVisual>?>(nameof(Waveforms));

    public static readonly StyledProperty<IReadOnlyList<AudioTimelineSegmentViewModel>?> TimelineSegmentsProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, IReadOnlyList<AudioTimelineSegmentViewModel>?>(nameof(TimelineSegments));

    public static readonly StyledProperty<bool> FreeViewportProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, bool>(nameof(FreeViewport));

    public static readonly StyledProperty<double> TrackGainDbProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(nameof(TrackGainDb));

    public static readonly StyledProperty<double> WaveformAmplitudeScaleProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, double>(
            nameof(WaveformAmplitudeScale),
            WaveformAmplitudeMath.Automatic,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> VisualRevisionProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, int>(nameof(VisualRevision));


    public static readonly StyledProperty<IReadOnlyList<TimelineThumbnailFrame>?> ThumbnailFramesProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, IReadOnlyList<TimelineThumbnailFrame>?>(nameof(ThumbnailFrames));

    public static readonly StyledProperty<TimelineBitmapVisual?> WaveformProperty =
        AvaloniaProperty.Register<SourceRangeCanvas, TimelineBitmapVisual?>(nameof(Waveform));

    private static readonly IBrush TrackBrush = new ImmutableSolidColorBrush(0xFF252936);
    private static readonly IBrush KeptBrush = new ImmutableSolidColorBrush(0x185B45BE);
    private static readonly IBrush RemovedBrush = new ImmutableSolidColorBrush(0xB8151820);
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(0x18FFFFFF);
    private static readonly IPen SelectionPen = new Pen(0xFFD8CCFF, 1).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(0xFFF4F5FA, 2).ToImmutable();
    private static readonly IBrush EdgeHandleBrush = new ImmutableSolidColorBrush(0xFFF4F5FA);
    private static readonly IPen SegmentBoundaryPen = new Pen(0xFF52CAE7, 1.5).ToImmutable();
    private static readonly IBrush SegmentGainBrush = new ImmutableSolidColorBrush(0xAA52CAE7);
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
            WaveformProperty,
            WaveformsProperty,
            TimelineSegmentsProperty,
            FreeViewportProperty,
            TrackGainDbProperty,
            WaveformAmplitudeScaleProperty,
            VisualRevisionProperty);
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
    public event EventHandler? DeleteRequested;


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

    public IReadOnlyList<TimelineBitmapVisual>? Waveforms
    {
        get => GetValue(WaveformsProperty);
        set => SetValue(WaveformsProperty, value);
    }

    public IReadOnlyList<AudioTimelineSegmentViewModel>? TimelineSegments
    {
        get => GetValue(TimelineSegmentsProperty);
        set => SetValue(TimelineSegmentsProperty, value);
    }

    public bool FreeViewport
    {
        get => GetValue(FreeViewportProperty);
        set => SetValue(FreeViewportProperty, value);
    }

    public double TrackGainDb
    {
        get => GetValue(TrackGainDbProperty);
        set => SetValue(TrackGainDbProperty, value);
    }

    public double WaveformAmplitudeScale
    {
        get => GetValue(WaveformAmplitudeScaleProperty);
        set => SetValue(WaveformAmplitudeScaleProperty, value);
    }

    public int VisualRevision
    {
        get => GetValue(VisualRevisionProperty);
        set => SetValue(VisualRevisionProperty, value);
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

        DrawAnalysisVisuals(context);
        DrawTimelineSegments(context);
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
        if (point.Properties.IsMiddleButtonPressed && Duration > 0 && (EffectiveZoom > 1 || FreeViewport))
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

        var action = TimelineWheelInteraction.Resolve(eventArgs.KeyModifiers, isWaveform: true);
        if (action == TimelineWheelAction.ScaleWaveform)
        {
            SetCurrentValue(
                WaveformAmplitudeScaleProperty,
                WaveformAmplitudeMath.Adjust(
                    WaveformAmplitudeScale,
                    EffectiveViewportDuration,
                    eventArgs.Delta.Y));
        }
        else if (action == TimelineWheelAction.PanTime && (EffectiveZoom > 1 || FreeViewport))
        {
            SetCurrentValue(
                ViewportStartProperty,
                ClampViewportStart(EffectiveViewportStart - (eventArgs.Delta.Y * EffectiveViewportDuration * 0.12)));
        }
        else if (action == TimelineWheelAction.ZoomTime)
        {
            var pointerX = eventArgs.GetPosition(this).X;
            var anchor = XToTime(pointerX);
            var requestedZoom = Math.Clamp(
                EffectiveZoom * Math.Pow(1.25, eventArgs.Delta.Y),
                FreeViewport ? TimelineViewportMath.MinimumFreeZoom : 1,
                TimelineViewportMath.MaximumZoom);
            var relative = Math.Clamp(pointerX / Math.Max(1, Bounds.Width), 0, 1);
            var newDuration = Duration / requestedZoom;
            SetCurrentValue(ZoomProperty, requestedZoom);
            SetCurrentValue(ViewportStartProperty, ClampViewportStart(anchor - (relative * newDuration), requestedZoom));
        }

        else
        {
            // Leave an unmodified wheel event for the containing editor ScrollViewer.
            // The mixer can have many tracks, so vertical navigation takes priority here.
            return;
        }

        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Delete)
        {
            DeleteRequested?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
            return;
        }

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

    private double EffectiveZoom => TimelineViewportMath.ClampZoom(Zoom, FreeViewport);

    private double EffectiveViewportDuration => Duration <= 0
        ? 1
        : TimelineViewportMath.VisibleDuration(Duration, EffectiveZoom, FreeViewport);

    private double EffectiveViewportStart => ClampViewportStart(ViewportStart);

    private double EffectiveViewportEnd => EffectiveViewportStart + EffectiveViewportDuration;

    private bool IsVisibleTime(double seconds) =>
        seconds >= EffectiveViewportStart && seconds <= EffectiveViewportEnd;

    private double ClampViewportStart(double start, double? zoom = null)
    {
        return TimelineViewportMath.ClampStart(Duration, zoom ?? EffectiveZoom, start, FreeViewport);
    }

    private void DrawAnalysisVisuals(DrawingContext context)
    {
        using var clip = context.PushClip(new Rect(Bounds.Size));
        if (Waveform is { } waveform)
        {
            DrawBitmapRange(context, waveform.Image, waveform.Start, waveform.End, waveform.Start, waveform.End, TrackGainDb);
        }

        foreach (var timelineWaveform in Waveforms ?? [])
        {
            DrawTimelineWaveform(context, timelineWaveform);
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

    private void DrawTimelineWaveform(DrawingContext context, TimelineBitmapVisual waveform)
    {
        var cursor = waveform.Start;
        foreach (var segment in (TimelineSegments ?? [])
                     .Where(segment => segment.TimelineEndSeconds > waveform.Start &&
                                       segment.TimelineStartSeconds < waveform.End)
                     .OrderBy(segment => segment.TimelineStartSeconds))
        {
            var segmentStart = Math.Max(waveform.Start, segment.TimelineStartSeconds);
            var segmentEnd = Math.Min(waveform.End, segment.TimelineEndSeconds);
            if (segmentStart > cursor)
            {
                DrawBitmapRange(
                    context,
                    waveform.Image,
                    waveform.Start,
                    waveform.End,
                    cursor,
                    segmentStart,
                    TrackGainDb);
            }

            DrawBitmapRange(
                context,
                waveform.Image,
                waveform.Start,
                waveform.End,
                segmentStart,
                segmentEnd,
                Math.Clamp(TrackGainDb + segment.GainDb, -60, 12));
            cursor = Math.Max(cursor, segmentEnd);
        }

        if (cursor < waveform.End)
        {
            DrawBitmapRange(
                context,
                waveform.Image,
                waveform.Start,
                waveform.End,
                cursor,
                waveform.End,
                TrackGainDb);
        }
    }

    private void DrawBitmapRange(
        DrawingContext context,
        Avalonia.Media.Imaging.Bitmap image,
        double imageStart,
        double imageEnd,
        double requestedStart,
        double requestedEnd,
        double gainDb)
    {
        var visibleStart = Math.Max(requestedStart, EffectiveViewportStart);
        var visibleEnd = Math.Min(requestedEnd, EffectiveViewportEnd);
        if (visibleEnd <= visibleStart)
        {
            return;
        }

        var imageWidth = image.PixelSize.Width;
        var sourceLeft = ((visibleStart - imageStart) / (imageEnd - imageStart)) * imageWidth;
        var sourceRight = ((visibleEnd - imageStart) / (imageEnd - imageStart)) * imageWidth;
        var amplitudeScale = GainToWaveformScale(gainDb) *
                             WaveformAmplitudeMath.Resolve(
                                 WaveformAmplitudeScale,
                                 EffectiveViewportDuration);
        var destinationHeight = Bounds.Height * amplitudeScale;
        var destinationTop = (Bounds.Height - destinationHeight) / 2;
        context.DrawImage(
            image,
            new Rect(sourceLeft, 0, sourceRight - sourceLeft, image.PixelSize.Height),
            new Rect(
                TimeToX(visibleStart),
                destinationTop,
                TimeToX(visibleEnd) - TimeToX(visibleStart),
                destinationHeight));
    }

    internal static double GainToWaveformScale(double gainDb)
    {
        var bounded = Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, -60, 12);
        return Math.Pow(10, bounded / 40);
    }

    private void DrawTimelineSegments(DrawingContext context)
    {
        foreach (var segment in TimelineSegments ?? [])
        {
            var start = Math.Max(segment.TimelineStartSeconds, EffectiveViewportStart);
            var end = Math.Min(segment.TimelineEndSeconds, EffectiveViewportEnd);
            if (end <= start)
            {
                continue;
            }

            var rect = new Rect(
                TimeToX(start),
                1,
                Math.Max(1, TimeToX(end) - TimeToX(start)),
                Math.Max(0, Bounds.Height - 2));
            context.DrawRectangle(null, SegmentBoundaryPen, rect, 3);
            if (segment.IsGainAdjustable && Math.Abs(segment.GainDb) > 0.05)
            {
                context.FillRectangle(
                    SegmentGainBrush,
                    new Rect(rect.Left + 3, rect.Top + 3, 3, Math.Max(0, rect.Height - 6)),
                    1);
            }
        }
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
