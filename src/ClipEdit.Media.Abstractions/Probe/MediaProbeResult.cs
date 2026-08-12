using System.Collections.Immutable;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public sealed record MediaProbeResult
{
    public MediaProbeResult(
        string sourcePath,
        string formatName,
        string? formatLongName,
        MediaTime startTime,
        MediaTime? duration,
        long? fileSizeBytes,
        long? bitRateBitsPerSecond,
        ImmutableArray<MediaStreamInfo> streams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        SourcePath = sourcePath;
        FormatName = formatName;
        FormatLongName = formatLongName;
        StartTime = startTime;
        Duration = duration;
        FileSizeBytes = fileSizeBytes;
        BitRateBitsPerSecond = bitRateBitsPerSecond;
        Streams = streams.IsDefault ? [] : streams;
    }

    public string SourcePath { get; }

    public string FormatName { get; }

    public string? FormatLongName { get; }

    public MediaTime StartTime { get; }

    public MediaTime? Duration { get; }

    public long? FileSizeBytes { get; }

    public long? BitRateBitsPerSecond { get; }

    public ImmutableArray<MediaStreamInfo> Streams { get; }

    public IEnumerable<VideoStreamInfo> VideoStreams => Streams.OfType<VideoStreamInfo>();

    public IEnumerable<AudioStreamInfo> AudioStreams => Streams.OfType<AudioStreamInfo>();
}
