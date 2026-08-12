using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Preview;

public interface IPreviewEngine : IAsyncDisposable
{
    PreviewState State { get; }

    Task LoadAsync(string sourcePath, CancellationToken cancellationToken);

    Task SeekAsync(MediaTime position, CancellationToken cancellationToken);

    Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken);

    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
}
