using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using ClipEdit.App.ViewModels;

namespace ClipEdit.App.Controls;

/// <summary>
/// One-track, ripple sequence timeline with filmstrip frames, range selection,
/// non-destructive trim handles, zoom/pan, and hover scrubbing.
/// </summary>
public sealed class SequenceTimelineCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<VideoClipViewModel>?> ClipsProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, IReadOnlyList<VideoClipViewModel>?>(nameof(Clips));

    public static readonly StyledProperty<VideoClipViewModel?> SelectedClipProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, VideoClipViewModel?>(nameof(SelectedClip));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(Duration));

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(Playhead));

    public static readonly StyledProperty<double> SelectionStartProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(SelectionStart));

    public static readonly StyledProperty<double> SelectionEndProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(SelectionEnd));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(Zoom), 1);

    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(ViewportStart));

    public static readonly StyledProperty<double> HoverTimeProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(HoverTime), -1);

    public static readonly StyledProperty<Bitmap?> HoverImageProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, Bitmap?>(nameof(HoverImage));

    public static readonly StyledProperty<int> VisualRevisionProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, int>(nameof(VisualRevision));

    private static readonly IBrush TrackBrush = new ImmutableSolidColorBrush(0xFF282D38);
    private static readonly IBrush EmptyClipBrush = new ImmutableSolidColorBrush(0xFF40366C);
    private static readonly IBrush ClipTintBrush = new ImmutableSolidColorBrush(0x3D6F50E8);
    private static readonly IBrush UnselectedShadeBrush = new ImmutableSolidColorBrush(0x69090B10);
    private static readonly IBrush OutsideSelectionBrush = new ImmutableSolidColorBrush(0xB20A0C12);
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(0x326E51FF);
    private static readonly IBrush SelectionHandleBrush = new ImmutableSolidColorBrush(0xFFFFFFFF);
    private static readonly IBrush AvailableHandleBrush = new ImmutableSolidColorBrush(0xFFFFC857);
    private static readonly IPen SelectedClipPen = new Pen(0xFF9DE7FF, 3).ToImmutable();
    private static readonly IPen ClipBoundaryPen = new Pen(0xFF171A22, 2).ToImmutable();
    private static readonly IPen SelectionPen = new Pen(0xFFE8DEFF, 2.5).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(0xFFFF6D8A, 2).ToImmutable();
    private static readonly IPen HoverPen = new Pen(0xFF9DE7FF, 1).ToImmutable();
    private static readonly IPen HoverBorderPen = new Pen(0xFF9DE7FF, 2).ToImmutable();
    private const double TrackTop = 28;
    private const double EdgeHitWidth = 9;
    private const double SelectionBandHeight = 18;

    private SequenceTimelineDragMode _dragMode;
    private VideoClipViewModel? _dragClip;
    private double _pointerStartX;
    private double _dragSourceStart;
    private double _dragSourceEnd;
    private double _selectionAnchor;
    private bool _isPanning;
    private double _panStartX;
    private double _panViewportStart;

    static SequenceTimelineCanvas()
    {
        AffectsRender<SequenceTimelineCanvas>(
            ClipsProperty,
            SelectedClipProperty,
            DurationProperty,
            PlayheadProperty,
            SelectionStartProperty,
            SelectionEndProperty,
            ZoomProperty,
            ViewportStartProperty,
            HoverTimeProperty,
            HoverImageProperty,
            VisualRevisionProperty);
    }

    public SequenceTimelineCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public event EventHandler? DeleteRequested;

    public event EventHandler? SplitRequested;

    public event EventHandler? MoveLeftRequested;

    public event EventHandler? MoveRightRequested;

    public IReadOnlyList<VideoClipViewModel>? Clips
    {
        get => GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }

    public VideoClipViewModel? SelectedClip
    {
        get => GetValue(SelectedClipProperty);
        set => SetValue(SelectedClipProperty, value);
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

    public double HoverTime
    {
        get => GetValue(HoverTimeProperty);
        set => SetValue(HoverTimeProperty, value);
    }

    public Bitmap? HoverImage
    {
        get => GetValue(HoverImageProperty);
        set => SetValue(HoverImageProperty, value);
    }

    public int VisualRevision
    {
        get => GetValue(VisualRevisionProperty);
        set => SetValue(VisualRevisionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(TrackBrush, new Rect(0, TrackTop, Bounds.Width, TrackHeight), 6);
        if (Duration <= 0 || Bounds.Width <= 0 || TrackHeight <= 0)
        {
            return;
        }

        using var clipBounds = context.PushClip(new Rect(Bounds.Size));
        foreach (var clip in Clips ?? [])
        {
            DrawClip(context, clip);
        }

        DrawSelection(context);

        if (IsVisibleTime(Playhead))
        {
            var x = TimeToX(Playhead);
            context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        if (HoverTime >= 0 && IsVisibleTime(HoverTime))
        {
            var x = TimeToX(HoverTime);
            context.DrawLine(HoverPen, new Point(x, TrackTop), new Point(x, Bounds.Height));
            DrawHoverPreview(context, x);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        var currentPoint = eventArgs.GetCurrentPoint(this);
        if (currentPoint.Properties.IsMiddleButtonPressed && EffectiveZoom > 1)
        {
            Focus();
            _isPanning = true;
            _panStartX = eventArgs.GetPosition(this).X;
            _panViewportStart = EffectiveViewportStart;
            eventArgs.Pointer.Capture(this);
            eventArgs.Handled = true;
            return;
        }

        if (!currentPoint.Properties.IsLeftButtonPressed || Duration <= 0)
        {
            return;
        }

        Focus();
        var point = eventArgs.GetPosition(this);
        var time = XToTime(point.X);
        var normalizedSelectionStart = Math.Min(SelectionStart, SelectionEnd);
        var normalizedSelectionEnd = Math.Max(SelectionStart, SelectionEnd);
        if (point.Y <= SelectionBandHeight &&
            Math.Abs(point.X - TimeToX(normalizedSelectionStart)) <= EdgeHitWidth)
        {
            _dragMode = SequenceTimelineDragMode.SelectionStart;
        }
        else if (point.Y <= SelectionBandHeight &&
                 Math.Abs(point.X - TimeToX(normalizedSelectionEnd)) <= EdgeHitWidth)
        {
            _dragMode = SequenceTimelineDragMode.SelectionEnd;
        }
        else
        {
            _dragClip = FindClip(time);
            if (_dragClip is not null)
            {
                SetCurrentValue(SelectedClipProperty, _dragClip);
                var clipLeft = TimeToX(_dragClip.TimelineStartSeconds);
                var clipRight = TimeToX(_dragClip.TimelineEndSeconds);
                if (point.Y >= TrackTop && Math.Abs(point.X - clipLeft) <= EdgeHitWidth)
                {
                    _dragMode = SequenceTimelineDragMode.TrimStart;
                }
                else if (point.Y >= TrackTop && Math.Abs(point.X - clipRight) <= EdgeHitWidth)
                {
                    _dragMode = SequenceTimelineDragMode.TrimEnd;
                }
                else
                {
                    _dragMode = SequenceTimelineDragMode.NewSelection;
                }
            }
            else
            {
                _dragMode = SequenceTimelineDragMode.NewSelection;
            }
        }

        _pointerStartX = point.X;
        _selectionAnchor = time;
        _dragSourceStart = _dragClip?.SourceStartSeconds ?? 0;
        _dragSourceEnd = _dragClip?.SourceEndSeconds ?? 0;
        if (_dragMode == SequenceTimelineDragMode.NewSelection)
        {
            SetCurrentValue(SelectionStartProperty, time);
            SetCurrentValue(SelectionEndProperty, time);
            SetCurrentValue(PlayheadProperty, time);
        }

        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        var point = eventArgs.GetPosition(this);
        SetCurrentValue(HoverTimeProperty, XToTime(point.X));

        if (_isPanning && eventArgs.Pointer.Captured == this)
        {
            var deltaX = point.X - _panStartX;
            SetCurrentValue(
                ViewportStartProperty,
                ClampViewportStart(_panViewportStart - (deltaX * EffectiveViewportDuration / Math.Max(1, Bounds.Width))));
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Pointer.Captured != this)
        {
            return;
        }

        var time = XToTime(point.X);
        switch (_dragMode)
        {
            case SequenceTimelineDragMode.NewSelection:
                SetCurrentValue(SelectionStartProperty, Math.Min(_selectionAnchor, time));
                SetCurrentValue(SelectionEndProperty, Math.Max(_selectionAnchor, time));
                SetCurrentValue(PlayheadProperty, time);
                break;
            case SequenceTimelineDragMode.SelectionStart:
                SetCurrentValue(SelectionStartProperty, Math.Min(time, SelectionEnd));
                SetCurrentValue(PlayheadProperty, Math.Min(time, SelectionEnd));
                break;
            case SequenceTimelineDragMode.SelectionEnd:
                SetCurrentValue(SelectionEndProperty, Math.Max(time, SelectionStart));
                SetCurrentValue(PlayheadProperty, Math.Max(time, SelectionStart));
                break;
            case SequenceTimelineDragMode.TrimStart:
            case SequenceTimelineDragMode.TrimEnd:
                ApplyTrim(point.X - _pointerStartX);
                break;
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }

        _isPanning = false;
        if (_dragMode == SequenceTimelineDragMode.NewSelection)
        {
            _dragMode = SequenceTimelineDragMode.None;
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _isPanning = false;
        _dragMode = SequenceTimelineDragMode.None;
        _dragClip = null;
    }

    protected override void OnPointerExited(PointerEventArgs eventArgs)
    {
        base.OnPointerExited(eventArgs);
        if (eventArgs.Pointer.Captured != this)
        {
            SetCurrentValue(HoverTimeProperty, -1);
        }
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
            var x = eventArgs.GetPosition(this).X;
            var anchor = XToTime(x);
            var requestedZoom = Math.Clamp(
                EffectiveZoom * Math.Pow(1.25, eventArgs.Delta.Y),
                1,
                TimelineViewportMath.MaximumZoom);
            var relative = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1);
            var newDuration = Duration / requestedZoom;
            SetCurrentValue(ZoomProperty, requestedZoom);
            SetCurrentValue(ViewportStartProperty, ClampViewportStart(anchor - (relative * newDuration), requestedZoom));
        }

        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.Key == Key.Delete)
        {
            DeleteRequested?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.S && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SplitRequested?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) && eventArgs.Key is Key.Left or Key.Right)
        {
            if (eventArgs.Key == Key.Left)
            {
                MoveLeftRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                MoveRightRequested?.Invoke(this, EventArgs.Empty);
            }

            eventArgs.Handled = true;
            return;
        }

        if (_dragClip is not null &&
            _dragMode is SequenceTimelineDragMode.TrimStart or SequenceTimelineDragMode.TrimEnd &&
            eventArgs.Key is Key.Left or Key.Right)
        {
            var direction = eventArgs.Key == Key.Left ? -1 : 1;
            var multiplier = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var delta = direction * multiplier * Math.Max(0.000001, _dragClip.Source.FrameStepSeconds);
            TrySetTrim(_dragMode == SequenceTimelineDragMode.TrimStart
                ? _dragClip.SourceStartSeconds + delta
                : _dragClip.SourceEndSeconds + delta);
            eventArgs.Handled = true;
            return;
        }

        var step = SelectedClip?.Source.FrameStepSeconds ?? (1d / 30);
        var playhead = eventArgs.Key switch
        {
            Key.Left => Playhead - step,
            Key.Right => Playhead + step,
            Key.Home => 0,
            Key.End => Duration,
            _ => Playhead,
        };
        if (playhead != Playhead)
        {
            SetCurrentValue(PlayheadProperty, Math.Clamp(playhead, 0, Duration));
            eventArgs.Handled = true;
        }
    }

    private void DrawClip(DrawingContext context, VideoClipViewModel clip)
    {
        var start = Math.Max(clip.TimelineStartSeconds, EffectiveViewportStart);
        var end = Math.Min(clip.TimelineEndSeconds, EffectiveViewportEnd);
        if (end <= start)
        {
            return;
        }

        var rect = new Rect(TimeToX(start), TrackTop, TimeToX(end) - TimeToX(start), TrackHeight);
        context.FillRectangle(EmptyClipBrush, rect, 4);
        using (context.PushClip(rect))
        {
            foreach (var frame in clip.TimelineThumbnails)
            {
                var frameTimelineStart = clip.TimelineStartSeconds + (frame.Start - clip.SourceStartSeconds);
                var frameTimelineEnd = clip.TimelineStartSeconds + (frame.End - clip.SourceStartSeconds);
                var visibleFrameStart = Math.Max(start, frameTimelineStart);
                var visibleFrameEnd = Math.Min(end, frameTimelineEnd);
                if (visibleFrameEnd <= visibleFrameStart)
                {
                    continue;
                }

                var destination = new Rect(
                    TimeToX(visibleFrameStart),
                    TrackTop,
                    TimeToX(visibleFrameEnd) - TimeToX(visibleFrameStart),
                    TrackHeight);
                context.DrawImage(
                    frame.Image,
                    CreateCoverSourceRect(frame.Image.PixelSize.Width, frame.Image.PixelSize.Height, destination.Width, destination.Height),
                    destination);
            }

            context.FillRectangle(ClipTintBrush, rect);
            if (!ReferenceEquals(clip, SelectedClip))
            {
                context.FillRectangle(UnselectedShadeBrush, rect);
            }
        }

        context.DrawRectangle(
            null,
            ReferenceEquals(clip, SelectedClip) ? SelectedClipPen : ClipBoundaryPen,
            rect,
            4);

        if (clip.HasHeadHandle)
        {
            context.FillRectangle(AvailableHandleBrush, new Rect(rect.Left + 3, rect.Top + 3, 3, rect.Height - 6), 2);
        }

        if (clip.HasTailHandle)
        {
            context.FillRectangle(AvailableHandleBrush, new Rect(rect.Right - 6, rect.Top + 3, 3, rect.Height - 6), 2);
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        var selectionStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), EffectiveViewportStart, EffectiveViewportEnd);
        var selectionEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), EffectiveViewportStart, EffectiveViewportEnd);
        if (selectionEnd <= selectionStart)
        {
            return;
        }

        var left = TimeToX(selectionStart);
        var right = TimeToX(selectionEnd);
        if (left > 0)
        {
            context.FillRectangle(OutsideSelectionBrush, new Rect(0, 0, left, Bounds.Height));
        }

        if (right < Bounds.Width)
        {
            context.FillRectangle(OutsideSelectionBrush, new Rect(right, 0, Bounds.Width - right, Bounds.Height));
        }

        var selectionRect = new Rect(left, 1, Math.Max(1, right - left), Math.Max(0, Bounds.Height - 2));
        context.FillRectangle(SelectionBrush, selectionRect, 4);
        context.DrawRectangle(null, SelectionPen, selectionRect, 4);
        context.FillRectangle(SelectionHandleBrush, new Rect(left - 3, 0, 6, Bounds.Height), 2);
        context.FillRectangle(SelectionHandleBrush, new Rect(right - 3, 0, 6, Bounds.Height), 2);
    }

    private void DrawHoverPreview(DrawingContext context, double pointerX)
    {
        if (HoverImage is null)
        {
            return;
        }

        const double width = 168;
        const double height = 94;
        var left = Math.Clamp(pointerX - (width / 2), 2, Math.Max(2, Bounds.Width - width - 2));
        var top = Math.Max(2, TrackTop + ((TrackHeight - height) / 2));
        var destination = new Rect(left, top, width, Math.Min(height, Bounds.Height - top - 2));
        context.FillRectangle(Brushes.Black, destination, 5);
        context.DrawImage(
            HoverImage,
            CreateCoverSourceRect(HoverImage.PixelSize.Width, HoverImage.PixelSize.Height, destination.Width, destination.Height),
            destination);
        context.DrawRectangle(null, HoverBorderPen, destination, 5);
    }

    private void ApplyTrim(double pointerDeltaX)
    {
        if (_dragClip is null)
        {
            return;
        }

        var sourceDelta = pointerDeltaX * EffectiveViewportDuration / Math.Max(1, Bounds.Width);
        TrySetTrim(_dragMode == SequenceTimelineDragMode.TrimStart
            ? _dragSourceStart + sourceDelta
            : _dragSourceEnd + sourceDelta);
    }

    private void TrySetTrim(double requestedSourceTime)
    {
        if (_dragClip is null)
        {
            return;
        }

        var frameStep = Math.Max(0.000001, _dragClip.Source.FrameStepSeconds);
        try
        {
            if (_dragMode == SequenceTimelineDragMode.TrimStart)
            {
                var bounded = Math.Clamp(
                    requestedSourceTime,
                    _dragClip.Model.AvailableRange.Start.TotalSeconds,
                    _dragClip.SourceEndSeconds - frameStep);
                _dragClip.SourceStartSeconds = bounded;
                SetCurrentValue(PlayheadProperty, _dragClip.TimelineStartSeconds);
            }
            else
            {
                var bounded = Math.Clamp(
                    requestedSourceTime,
                    _dragClip.SourceStartSeconds + frameStep,
                    _dragClip.Model.AvailableRange.End.TotalSeconds);
                _dragClip.SourceEndSeconds = bounded;
                SetCurrentValue(PlayheadProperty, _dragClip.TimelineEndSeconds);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private VideoClipViewModel? FindClip(double timelineSeconds) =>
        (Clips ?? []).FirstOrDefault(clip =>
            timelineSeconds >= clip.TimelineStartSeconds && timelineSeconds < clip.TimelineEndSeconds) ??
        ((Clips?.Count ?? 0) > 0 && Math.Abs(timelineSeconds - Clips![^1].TimelineEndSeconds) < 0.000001
            ? Clips[^1]
            : null);

    private double TimeToX(double seconds) =>
        ((seconds - EffectiveViewportStart) / EffectiveViewportDuration) * Bounds.Width;

    private double XToTime(double x) =>
        EffectiveViewportStart +
        (Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * EffectiveViewportDuration);

    private bool IsVisibleTime(double time) => time >= EffectiveViewportStart && time <= EffectiveViewportEnd;

    private double TrackHeight => Math.Max(0, Bounds.Height - TrackTop);

    private double EffectiveZoom => TimelineViewportMath.ClampZoom(Zoom);

    private double EffectiveViewportDuration => Duration <= 0 ? 1 : Duration / EffectiveZoom;

    private double EffectiveViewportStart => ClampViewportStart(ViewportStart);

    private double EffectiveViewportEnd => EffectiveViewportStart + EffectiveViewportDuration;

    private double ClampViewportStart(double start, double? zoom = null) =>
        TimelineViewportMath.ClampStart(Duration, zoom ?? EffectiveZoom, start);

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

internal enum SequenceTimelineDragMode
{
    None,
    NewSelection,
    SelectionStart,
    SelectionEnd,
    TrimStart,
    TrimEnd,
}
