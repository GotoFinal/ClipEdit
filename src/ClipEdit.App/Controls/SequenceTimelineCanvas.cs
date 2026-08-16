using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Domain.Timeline;

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

    public static readonly StyledProperty<bool> FreeViewportProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, bool>(nameof(FreeViewport));

    public static readonly StyledProperty<bool> SnappingEnabledProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, bool>(nameof(SnappingEnabled), true);

    public static readonly StyledProperty<bool> MoveClipsByDefaultProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, bool>(nameof(MoveClipsByDefault));

    public static readonly StyledProperty<bool> FastCutSnappingProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, bool>(nameof(FastCutSnapping));

    public static readonly StyledProperty<double> HoverTimeProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, double>(nameof(HoverTime), -1);

    public static readonly StyledProperty<int> VisualRevisionProperty =
        AvaloniaProperty.Register<SequenceTimelineCanvas, int>(nameof(VisualRevision));

    private static readonly IBrush TrackBrush = new ImmutableSolidColorBrush(0xFF282D38);
    private static readonly IBrush EmptyClipBrush = new ImmutableSolidColorBrush(0xFF40366C);
    private static readonly IBrush ClipTintBrush = new ImmutableSolidColorBrush(0x3D6F50E8);
    private static readonly IBrush UnselectedShadeBrush = new ImmutableSolidColorBrush(0x69090B10);
    private static readonly IBrush OutsideSelectionBrush = new ImmutableSolidColorBrush(0xB20A0C12);
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(0x326E51FF);
    private static readonly IBrush SelectionHandleBrush = new ImmutableSolidColorBrush(0xFFFFFFFF);
    private static readonly IBrush ClipTrimHandleBrush = new ImmutableSolidColorBrush(0xFF9DE7FF);
    private static readonly IBrush AvailableHandleBrush = new ImmutableSolidColorBrush(0xFFFFC857);
    private static readonly IPen SelectedClipPen = new Pen(0xFF9DE7FF, 3).ToImmutable();
    private static readonly IPen ClipBoundaryPen = new Pen(0xFF171A22, 2).ToImmutable();
    private static readonly IPen SelectionPen = new Pen(0xFFE8DEFF, 2.5).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(0xFFFF6D8A, 2).ToImmutable();
    private static readonly IPen HoverPen = new Pen(0xFF9DE7FF, 1).ToImmutable();
    private const double TrackTop = 28;
    private const double EdgeHitWidth = 14;

    private SequenceTimelineDragMode _dragMode;
    private VideoClipViewModel? _dragClip;
    private double _pointerStartX;
    private double _dragSourceStart;
    private double _dragSourceEnd;
    private double _dragTimelineStart;
    private double _previewTimelineStart = double.NaN;
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
            FreeViewportProperty,
            SnappingEnabledProperty,
            MoveClipsByDefaultProperty,
            FastCutSnappingProperty,
            HoverTimeProperty,
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
    public event EventHandler? CopyRequested;
    public event EventHandler? PasteRequested;
    public event EventHandler<VideoClipMoveRequestedEventArgs>? ClipMoveRequested;


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
    public bool FreeViewport
    {
        get => GetValue(FreeViewportProperty);
        set => SetValue(FreeViewportProperty, value);
    }

    public bool SnappingEnabled
    {
        get => GetValue(SnappingEnabledProperty);
        set => SetValue(SnappingEnabledProperty, value);
    }

    public bool MoveClipsByDefault
    {
        get => GetValue(MoveClipsByDefaultProperty);
        set => SetValue(MoveClipsByDefaultProperty, value);
    }

    public bool FastCutSnapping
    {
        get => GetValue(FastCutSnappingProperty);
        set => SetValue(FastCutSnappingProperty, value);
    }


    public double HoverTime
    {
        get => GetValue(HoverTimeProperty);
        set => SetValue(HoverTimeProperty, value);
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
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        var currentPoint = eventArgs.GetCurrentPoint(this);
        if (currentPoint.Properties.IsMiddleButtonPressed && (EffectiveZoom > 1 || FreeViewport))
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
        _dragClip = null;
        var normalizedSelectionStart = Math.Min(SelectionStart, SelectionEnd);
        var normalizedSelectionEnd = Math.Max(SelectionStart, SelectionEnd);
        var hasSelection = normalizedSelectionEnd - normalizedSelectionStart > 0.000001;
        if (hasSelection &&
            point.Y < TrackTop &&
            Math.Abs(point.X - TimeToX(normalizedSelectionStart)) <= EdgeHitWidth)
        {
            _dragMode = SequenceTimelineDragMode.SelectionStart;
        }
        else if (hasSelection &&
                 point.Y < TrackTop &&
                  Math.Abs(point.X - TimeToX(normalizedSelectionEnd)) <= EdgeHitWidth)
        {
            _dragMode = SequenceTimelineDragMode.SelectionEnd;
        }
        else
        {
            var trimHit = FindTrimHit(point);
            if (trimHit is { } hit)
            {
                _dragClip = hit.Clip;
                SetCurrentValue(SelectedClipProperty, _dragClip);
                _dragMode = hit.Mode;
            }
            else
            {
                _dragClip = point.Y >= TrackTop ? FindClip(time) : null;
                if (_dragClip is not null)
                {
                    SetCurrentValue(SelectedClipProperty, _dragClip);
                }

                if (_dragClip is not null &&
                    ShouldMoveClip(MoveClipsByDefault, eventArgs.KeyModifiers))
                {
                    SetCurrentValue(PlayheadProperty, time);
                    _dragMode = SequenceTimelineDragMode.MoveClip;
                }
                else
                {
                    _dragMode = SequenceTimelineDragMode.NewSelection;
                }
            }
        }

        _pointerStartX = point.X;
        _selectionAnchor = FastCutSnapping ? SnapTimelineCut(time) : time;
        _dragSourceStart = _dragClip?.SourceStartSeconds ?? 0;
        _dragSourceEnd = _dragClip?.SourceEndSeconds ?? 0;
        _dragTimelineStart = _dragClip?.TimelineStartSeconds ?? 0;
        _previewTimelineStart = _dragTimelineStart;
        if (_dragMode == SequenceTimelineDragMode.NewSelection)
        {
            SetCurrentValue(SelectionStartProperty, _selectionAnchor);
            SetCurrentValue(SelectionEndProperty, _selectionAnchor);
            SetCurrentValue(PlayheadProperty, _selectionAnchor);
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
        var cutTime = FastCutSnapping ? SnapTimelineCut(time) : time;
        switch (_dragMode)
        {
            case SequenceTimelineDragMode.NewSelection:
                SetCurrentValue(SelectionStartProperty, Math.Min(_selectionAnchor, cutTime));
                SetCurrentValue(SelectionEndProperty, Math.Max(_selectionAnchor, cutTime));
                SetCurrentValue(PlayheadProperty, cutTime);
                break;
            case SequenceTimelineDragMode.SelectionStart:
                SetCurrentValue(SelectionStartProperty, Math.Min(cutTime, SelectionEnd));
                SetCurrentValue(PlayheadProperty, Math.Min(cutTime, SelectionEnd));
                break;
            case SequenceTimelineDragMode.SelectionEnd:
                SetCurrentValue(SelectionEndProperty, Math.Max(cutTime, SelectionStart));
                SetCurrentValue(PlayheadProperty, Math.Max(cutTime, SelectionStart));
                break;
            case SequenceTimelineDragMode.TrimStart:
            case SequenceTimelineDragMode.TrimEnd:
                ApplyTrim(point.X - _pointerStartX);
                break;
            case SequenceTimelineDragMode.MoveClip:
                var requestedStart = Math.Max(
                    0,
                    _dragTimelineStart + ((point.X - _pointerStartX) * EffectiveViewportDuration / Math.Max(1, Bounds.Width)));
                _previewTimelineStart = SnapClipTimelineStart(
                    requestedStart,
                    SnappingEnabled && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Alt));
                InvalidateVisual();
                break;
        }

        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);

        _isPanning = false;
        if (_dragMode == SequenceTimelineDragMode.MoveClip &&
            _dragClip is { } movedClip &&
            double.IsFinite(_previewTimelineStart))
        {
            ClipMoveRequested?.Invoke(
                this,
                new VideoClipMoveRequestedEventArgs(movedClip, _previewTimelineStart));
        }
        if (eventArgs.Pointer.Captured == this)
        {
            eventArgs.Pointer.Capture(null);
        }
        _dragMode = SequenceTimelineDragMode.None;
        _dragClip = null;
        _previewTimelineStart = double.NaN;
        InvalidateVisual();

        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        _isPanning = false;
        _dragMode = SequenceTimelineDragMode.None;
        _dragClip = null;
        _previewTimelineStart = double.NaN;
        InvalidateVisual();
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

        var action = TimelineWheelInteraction.Resolve(eventArgs.KeyModifiers, isWaveform: false);
        if (action == TimelineWheelAction.PanTime && (EffectiveZoom > 1 || FreeViewport))
        {
            SetCurrentValue(
                ViewportStartProperty,
                ClampViewportStart(EffectiveViewportStart - (eventArgs.Delta.Y * EffectiveViewportDuration * 0.12)));
        }
        else if (action == TimelineWheelAction.ZoomTime)
        {
            var x = eventArgs.GetPosition(this).X;
            var anchor = XToTime(x);
            var requestedZoom = Math.Clamp(
                EffectiveZoom * Math.Pow(1.25, eventArgs.Delta.Y),
                FreeViewport ? TimelineViewportMath.MinimumFreeZoom : 1,
                TimelineViewportMath.MaximumZoom);
            var relative = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1);
            var newDuration = TimelineViewportMath.VisibleDuration(Duration, requestedZoom, FreeViewport);
            SetCurrentValue(ZoomProperty, requestedZoom);
            SetCurrentValue(ViewportStartProperty, ClampViewportStart(anchor - (relative * newDuration), requestedZoom));
        }

        else
        {
            return;
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

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) && eventArgs.Key == Key.C)
        {
            CopyRequested?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) && eventArgs.Key == Key.V)
        {
            PasteRequested?.Invoke(this, EventArgs.Empty);
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

        var step = SelectedClip is { } selected
            ? selected.Model.SourceDurationToTimeline(
                new MediaTime(
                    checked((long)Math.Round(selected.Source.FrameStepSeconds * 1_000_000)),
                    1_000_000)).TotalSeconds
            : 1d / 30;
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
        var clipStart = ReferenceEquals(clip, _dragClip) &&
                        _dragMode == SequenceTimelineDragMode.MoveClip
            ? _previewTimelineStart
            : clip.TimelineStartSeconds;
        var start = Math.Max(clipStart, EffectiveViewportStart);
        var end = Math.Min(clipStart + clip.DurationSeconds, EffectiveViewportEnd);
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
                var frameTimelineStart = clip.Model.SourceTimeToTimeline(
                    ToMediaTime(frame.Start)).TotalSeconds;
                var frameTimelineEnd = clip.Model.SourceTimeToTimeline(
                    ToMediaTime(frame.End)).TotalSeconds;
                if (clipStart != clip.TimelineStartSeconds)
                {
                    var moveDelta = clipStart - clip.TimelineStartSeconds;
                    frameTimelineStart += moveDelta;
                    frameTimelineEnd += moveDelta;
                }
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

        if (ReferenceEquals(clip, SelectedClip))
        {
            var handleHeight = Math.Min(24, Math.Max(12, rect.Height - 10));
            var handleTop = rect.Center.Y - (handleHeight / 2);
            context.FillRectangle(
                ClipTrimHandleBrush,
                new Rect(rect.Left - 2, handleTop, 6, handleHeight),
                2);
            context.FillRectangle(
                ClipTrimHandleBrush,
                new Rect(rect.Right - 4, handleTop, 6, handleHeight),
                2);
        }

        if (FastCutSnapping && clip.Source.IsKeyframeIndexReady)
        {
            foreach (var keyframe in clip.Source.VideoKeyframeSeconds
                         .Where(sourceTime => sourceTime >= clip.SourceStartSeconds && sourceTime <= clip.SourceEndSeconds)
                         .Select(sourceTime => clip.Model.SourceTimeToTimeline(ToMediaTime(sourceTime)).TotalSeconds)
                         .Where(IsVisibleTime)
                         .Take(600))
            {
                var x = TimeToX(keyframe);
                context.DrawLine(
                    HoverPen,
                    new Point(x, TrackTop),
                    new Point(x, Math.Min(Bounds.Height, TrackTop + 5)));
            }
        }
    }

    internal static bool ShouldMoveClip(bool moveClipsByDefault, KeyModifiers modifiers) =>
        moveClipsByDefault || modifiers.HasFlag(KeyModifiers.Control);

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
        context.DrawLine(SelectionPen, new Point(left, 0), new Point(left, Bounds.Height));
        context.DrawLine(SelectionPen, new Point(right, 0), new Point(right, Bounds.Height));
        context.FillRectangle(SelectionHandleBrush, new Rect(left - 5, 0, 10, TrackTop - 3), 3);
        context.FillRectangle(SelectionHandleBrush, new Rect(right - 5, 0, 10, TrackTop - 3), 3);
    }

    private void ApplyTrim(double pointerDeltaX)
    {
        if (_dragClip is null)
        {
            return;
        }

        var timelineDelta = pointerDeltaX * EffectiveViewportDuration / Math.Max(1, Bounds.Width);
        var sourceDelta = timelineDelta * _dragClip.PlaybackSpeed;
        TrySetTrim(_dragMode == SequenceTimelineDragMode.TrimStart
            ? _dragSourceStart + sourceDelta
            : _dragSourceEnd + sourceDelta);
    }

    private double SnapClipTimelineStart(double requestedStart, bool enabled)
    {
        if (!enabled || _dragClip is null)
        {
            return requestedStart;
        }

        var tolerance = 9 * EffectiveViewportDuration / Math.Max(1, Bounds.Width);
        return SnapTimelineStart(
            requestedStart,
            _dragClip.DurationSeconds,
            (Clips ?? [])
                .Where(clip => !ReferenceEquals(clip, _dragClip))
                .Select(clip => (clip.TimelineStartSeconds, clip.TimelineEndSeconds)),
            tolerance);
    }

    internal static double SnapTimelineStart(
        double requestedStart,
        double clipDuration,
        IEnumerable<(double Start, double End)> otherClips,
        double tolerance)
    {
        var requested = Math.Max(0, requestedStart);
        if (!double.IsFinite(requested) || !double.IsFinite(clipDuration) || clipDuration <= 0 ||
            !double.IsFinite(tolerance) || tolerance <= 0)
        {
            return requested;
        }

        var others = otherClips
            .Where(other => double.IsFinite(other.Start) && double.IsFinite(other.End) && other.End > other.Start)
            .ToArray();
        var targets = others
            .SelectMany(other => new[] { other.Start, other.End })
            .Append(0d);
        return targets
            .SelectMany(target => new[] { target, target - clipDuration })
            .Where(candidate => candidate >= 0 && Math.Abs(candidate - requested) <= tolerance)
            .Where(candidate => others.All(other =>
                candidate + clipDuration <= other.Start + 0.000001 ||
                candidate >= other.End - 0.000001))
            .OrderBy(candidate => Math.Abs(candidate - requested))
            .ThenBy(candidate => candidate)
            .Cast<double?>()
            .FirstOrDefault() ?? requested;
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
                var previousEnd = (Clips ?? [])
                    .Where(clip => !ReferenceEquals(clip, _dragClip) && clip.TimelineEndSeconds <= _dragClip.TimelineStartSeconds)
                    .Select(clip => clip.TimelineEndSeconds)
                    .DefaultIfEmpty(0)
                    .Max();
                var earliestSourceStart = _dragClip.SourceStartSeconds -
                                          ((_dragClip.TimelineStartSeconds - previousEnd) *
                                           _dragClip.PlaybackSpeed);
                var minimum = Math.Max(_dragClip.Model.AvailableRange.Start.TotalSeconds, earliestSourceStart);
                var maximum = _dragClip.SourceEndSeconds - frameStep;
                var bounded = FastCutSnapping
                    ? SnapSourceCut(_dragClip, requestedSourceTime, minimum, maximum)
                    : Math.Clamp(requestedSourceTime, minimum, maximum);
                _dragClip.SourceStartSeconds = bounded;
                SetCurrentValue(PlayheadProperty, _dragClip.TimelineStartSeconds);
            }
            else
            {
                var nextStart = (Clips ?? [])
                    .Where(clip => !ReferenceEquals(clip, _dragClip) && clip.TimelineStartSeconds >= _dragClip.TimelineEndSeconds)
                    .Select(clip => clip.TimelineStartSeconds)
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();
                var latestSourceEnd = double.IsPositiveInfinity(nextStart)
                    ? _dragClip.Model.AvailableRange.End.TotalSeconds
                    : _dragClip.SourceEndSeconds +
                      ((nextStart - _dragClip.TimelineEndSeconds) * _dragClip.PlaybackSpeed);
                var minimum = _dragClip.SourceStartSeconds + frameStep;
                var maximum = Math.Min(_dragClip.Model.AvailableRange.End.TotalSeconds, latestSourceEnd);
                var bounded = FastCutSnapping
                    ? SnapSourceCut(_dragClip, requestedSourceTime, minimum, maximum)
                    : Math.Clamp(requestedSourceTime, minimum, maximum);
                _dragClip.SourceEndSeconds = bounded;
                SetCurrentValue(PlayheadProperty, _dragClip.TimelineEndSeconds);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private double SnapTimelineCut(double requestedTime)
    {
        var candidates = (Clips ?? [])
            .SelectMany(clip => GetTimelineCopyBoundaries(clip))
            .Distinct()
            .ToArray();
        return candidates.Length == 0
            ? requestedTime
            : candidates
                .OrderBy(candidate => Math.Abs(candidate - requestedTime))
                .ThenBy(static candidate => candidate)
                .First();
    }

    private static double SnapSourceCut(
        VideoClipViewModel clip,
        double requestedSourceTime,
        double minimum,
        double maximum)
    {
        var duration = clip.Source.SourceDurationSeconds;
        var candidates = clip.Source.VideoKeyframeSeconds
            .Where(timestamp => timestamp >= minimum && timestamp <= maximum)
            .Concat(minimum <= 0 && maximum >= 0 ? [0d] : [])
            .Concat(duration >= minimum && duration <= maximum ? [duration] : [])
            .Distinct()
            .ToArray();
        return candidates.Length == 0
            ? Math.Clamp(requestedSourceTime, minimum, maximum)
            : candidates
                .OrderBy(candidate => Math.Abs(candidate - requestedSourceTime))
                .ThenBy(static candidate => candidate)
                .First();
    }

    private static IEnumerable<double> GetTimelineCopyBoundaries(VideoClipViewModel clip)
    {
        foreach (var timestamp in clip.Source.VideoKeyframes)
        {
            if (timestamp >= clip.Model.AvailableRange.Start && timestamp <= clip.Model.AvailableRange.End)
            {
                yield return clip.Model.SourceTimeToTimeline(timestamp).TotalSeconds;
            }
        }

        if (clip.Model.AvailableRange.Start == MediaTime.Zero)
        {
            yield return clip.Model.SourceTimeToTimeline(MediaTime.Zero).TotalSeconds;
        }
        var duration = clip.Source.Edit?.SourceDuration ?? clip.Source.Media?.Probe.Duration;
        if (duration is { } sourceDuration && clip.Model.AvailableRange.End == sourceDuration)
        {
            yield return clip.Model.SourceTimeToTimeline(clip.Model.AvailableRange.End).TotalSeconds;
        }
    }

    private VideoClipViewModel? FindClip(double timelineSeconds) =>
        (Clips ?? []).FirstOrDefault(clip =>
            timelineSeconds >= clip.TimelineStartSeconds && timelineSeconds < clip.TimelineEndSeconds) ??
        ((Clips?.Count ?? 0) > 0 && Math.Abs(timelineSeconds - Clips![^1].TimelineEndSeconds) < 0.000001
            ? Clips[^1]
            : null);

    private static MediaTime ToMediaTime(double seconds) =>
        new(checked((long)Math.Round(seconds * 1_000_000)), 1_000_000);

    private TimelineTrimHit? FindTrimHit(Point point)
    {
        if (point.Y < TrackTop)
        {
            return null;
        }

        var clips = Clips ?? [];
        if (SelectedClip is not null)
        {
            var selectedHit = GetTrimHit(SelectedClip, point.X);
            if (selectedHit is not null)
            {
                return selectedHit;
            }
        }

        TimelineTrimHit? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var clip in clips)
        {
            if (ReferenceEquals(clip, SelectedClip))
            {
                continue;
            }

            var leftDistance = Math.Abs(point.X - TimeToX(clip.TimelineStartSeconds));
            if (leftDistance <= EdgeHitWidth && leftDistance < nearestDistance)
            {
                nearest = new TimelineTrimHit(clip, SequenceTimelineDragMode.TrimStart);
                nearestDistance = leftDistance;
            }

            var rightDistance = Math.Abs(point.X - TimeToX(clip.TimelineEndSeconds));
            if (rightDistance <= EdgeHitWidth && rightDistance < nearestDistance)
            {
                nearest = new TimelineTrimHit(clip, SequenceTimelineDragMode.TrimEnd);
                nearestDistance = rightDistance;
            }
        }

        return nearest;
    }

    private TimelineTrimHit? GetTrimHit(VideoClipViewModel clip, double pointerX)
    {
        var leftDistance = Math.Abs(pointerX - TimeToX(clip.TimelineStartSeconds));
        var rightDistance = Math.Abs(pointerX - TimeToX(clip.TimelineEndSeconds));
        if (leftDistance > EdgeHitWidth && rightDistance > EdgeHitWidth)
        {
            return null;
        }

        return leftDistance <= rightDistance
            ? new TimelineTrimHit(clip, SequenceTimelineDragMode.TrimStart)
            : new TimelineTrimHit(clip, SequenceTimelineDragMode.TrimEnd);
    }

    private double TimeToX(double seconds) =>
        ((seconds - EffectiveViewportStart) / EffectiveViewportDuration) * Bounds.Width;

    private double XToTime(double x) =>
        EffectiveViewportStart +
        (Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * EffectiveViewportDuration);

    private bool IsVisibleTime(double time) => time >= EffectiveViewportStart && time <= EffectiveViewportEnd;

    private double TrackHeight => Math.Max(0, Bounds.Height - TrackTop);

    private double EffectiveZoom => TimelineViewportMath.ClampZoom(Zoom, FreeViewport);

    private double EffectiveViewportDuration => Duration <= 0 ? 1 : TimelineViewportMath.VisibleDuration(Duration, EffectiveZoom, FreeViewport);

    private double EffectiveViewportStart => ClampViewportStart(ViewportStart);

    private double EffectiveViewportEnd => EffectiveViewportStart + EffectiveViewportDuration;

    private double ClampViewportStart(double start, double? zoom = null) =>
        TimelineViewportMath.ClampStart(Duration, zoom ?? EffectiveZoom, start, FreeViewport);

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

internal readonly record struct TimelineTrimHit(
    VideoClipViewModel Clip,
    SequenceTimelineDragMode Mode);

internal enum SequenceTimelineDragMode
{
    None,
    NewSelection,
    SelectionStart,
    SelectionEnd,
    MoveClip,
    TrimStart,
    TrimEnd,
}

public sealed class VideoClipMoveRequestedEventArgs : EventArgs
{
    public VideoClipMoveRequestedEventArgs(VideoClipViewModel clip, double timelineStart)
    {
        Clip = clip ?? throw new ArgumentNullException(nameof(clip));
        TimelineStart = timelineStart;
    }

    public VideoClipViewModel Clip { get; }
    public double TimelineStart { get; }
}
