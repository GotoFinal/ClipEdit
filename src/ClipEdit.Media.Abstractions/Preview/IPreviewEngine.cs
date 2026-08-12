using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Preview;

public interface IPreviewEngine : IAsyncDisposable
{
    PreviewState State { get; }

    Task LoadAsync(string sourcePath, CancellationToken cancellationToken);

    Task SeekAsync(MediaTime position, CancellationToken cancellationToken);

    Task<MediaTime?> GetPositionAsync(CancellationToken cancellationToken);

    Task<PreviewPlaybackSnapshot> GetPlaybackSnapshotAsync(CancellationToken cancellationToken);

    Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken);

    Task StepFrameAsync(
        PreviewFrameStepDirection direction,
        CancellationToken cancellationToken);

    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);

    Task SetVideoTransformAsync(
        PreviewVideoTransform transform,
        CancellationToken cancellationToken);

    Task SetAudioTracksAsync(
        IReadOnlyList<PreviewAudioTrack> audioTracks,
        CancellationToken cancellationToken);
}

public enum PreviewFrameStepDirection
{
    Backward,
    Forward,
}

public readonly record struct PreviewVideoTransform
{
    public PreviewVideoTransform(
        double zoomFactor,
        double panX,
        double panY,
        int rotationDegrees,
        double scaleX = 1,
        double scaleY = 1)
    {
        if (!double.IsFinite(zoomFactor) || zoomFactor is < 0.01 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(zoomFactor));
        }

        if (!double.IsFinite(panX) || !double.IsFinite(panY))
        {
            throw new ArgumentOutOfRangeException(nameof(panX));
        }
        if (!IsValidScale(scaleX) || !IsValidScale(scaleY))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        }


        ZoomFactor = zoomFactor;
        PanX = panX;
        PanY = panY;
        RotationDegrees = ((rotationDegrees % 360) + 360) % 360;
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    public double ZoomFactor { get; }
    public double PanX { get; }
    public double PanY { get; }
    public int RotationDegrees { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    private static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale is >= 0.01 and <= 100;
}
