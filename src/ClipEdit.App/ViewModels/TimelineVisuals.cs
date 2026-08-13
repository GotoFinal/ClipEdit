using Avalonia.Media.Imaging;

namespace ClipEdit.App.ViewModels;

public sealed class TimelineThumbnailFrame : IDisposable
{
    public TimelineThumbnailFrame(double start, double end, double timestamp, Bitmap image)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (!double.IsFinite(timestamp) || timestamp < start || timestamp > end)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        Start = start;
        End = end;
        Timestamp = timestamp;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    public double Start { get; }

    public double End { get; }

    public double Timestamp { get; }

    public Bitmap Image { get; }

    public void Dispose() => Image.Dispose();
}

public sealed class TimelineBitmapVisual : IDisposable
{
    public TimelineBitmapVisual(double start, double end, Bitmap image)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        Start = start;
        End = end;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    public double Start { get; }

    public double End { get; }

    public Bitmap Image { get; }

    public void Dispose() => Image.Dispose();
}

internal static class TimelineViewportMath
{
    public const double MaximumZoom = 65_536;
    public const double MinimumFreeZoom = 0.25;
    private const double MinimumVisibleSequenceRatio = 0.1;

    public static double ClampZoom(double zoom, bool freeViewport = false) =>
        double.IsFinite(zoom)
            ? Math.Clamp(zoom, freeViewport ? MinimumFreeZoom : 1, MaximumZoom)
            : 1;

    public static double VisibleDuration(double duration, double zoom, bool freeViewport = false) =>
        duration <= 0
            ? 0
            : duration / ClampZoom(zoom, freeViewport);

    public static double ClampStart(
        double duration,
        double zoom,
        double start,
        bool freeViewport = false)
    {
        if (!double.IsFinite(start) || duration <= 0)
        {
            return 0;
        }

        var visibleDuration = VisibleDuration(duration, zoom, freeViewport);
        if (freeViewport)
        {
            var minimumStart = -(visibleDuration * (1 - MinimumVisibleSequenceRatio));
            var maximumStart = duration - (visibleDuration * MinimumVisibleSequenceRatio);
            return Math.Clamp(start, minimumStart, maximumStart);
        }

        return Math.Clamp(start, 0, Math.Max(0, duration - visibleDuration));
    }

    public static (double Zoom, double Start) ZoomAround(
        double duration,
        double currentZoom,
        double currentStart,
        double requestedZoom,
        double anchor,
        bool freeViewport = false)
    {
        var oldZoom = ClampZoom(currentZoom, freeViewport);
        var newZoom = ClampZoom(requestedZoom, freeViewport);
        if (duration <= 0)
        {
            return (newZoom, 0);
        }

        var oldDuration = VisibleDuration(duration, oldZoom, freeViewport);
        var oldStart = ClampStart(duration, oldZoom, currentStart, freeViewport);
        var boundedAnchor = Math.Clamp(anchor, oldStart, oldStart + oldDuration);
        var relative = oldDuration <= 0 ? 0.5 : (boundedAnchor - oldStart) / oldDuration;
        var newDuration = VisibleDuration(duration, newZoom, freeViewport);
        var newStart = ClampStart(
            duration,
            newZoom,
            boundedAnchor - (relative * newDuration),
            freeViewport);
        return (newZoom, newStart);
    }
}
