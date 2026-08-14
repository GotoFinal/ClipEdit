using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Frames;

public interface IFrameDecoder
{
    Task<DecodedFrame> DecodeAsync(
        string sourcePath,
        int videoStreamIndex,
        MediaTime timestamp,
        PixelSize maximumSize,
        CancellationToken cancellationToken = default,
        bool toneMapHdr = false);
}
