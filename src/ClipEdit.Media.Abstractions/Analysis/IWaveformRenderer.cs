using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Analysis;

public interface IWaveformRenderer
{
    Task<WaveformImage> RenderAsync(
        string sourcePath,
        int audioStreamIndex,
        MediaRange visibleRange,
        PixelSize outputSize,
        CancellationToken cancellationToken = default);
}
