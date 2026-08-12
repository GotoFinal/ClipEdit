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

    Task SetAudioTracksAsync(
        IReadOnlyList<PreviewAudioTrack> audioTracks,
        CancellationToken cancellationToken);
}

public enum PreviewFrameStepDirection
{
    Backward,
    Forward,
}
