using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Preview;

public readonly record struct PreviewPlaybackSnapshot(
    MediaTime? Position,
    bool IsEndOfFile,
    string? HardwareDecoder = null);
