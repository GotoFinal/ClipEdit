using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Preview;

public sealed record PreviewAudioTrack
{
    public PreviewAudioTrack(int streamIndex, double gainDb, bool isMuted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        if (!double.IsFinite(gainDb) || gainDb is < -60 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        }

        StreamIndex = streamIndex;
        GainDb = gainDb;
        IsMuted = isMuted;
    }

    public PreviewAudioTrack(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        bool isMuted)
        : this(externalSourcePath, streamIndex, gainDb, isMuted, MediaTime.Zero)
    {
    }

    public PreviewAudioTrack(
        string externalSourcePath,
        int streamIndex,
        double gainDb,
        bool isMuted,
        MediaTime timelineOffset)
        : this(streamIndex, gainDb, isMuted)
    {
        if (timelineOffset < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timelineOffset),
                "An external audio timeline offset cannot be negative.");
        }

        ExternalSourcePath = ValidateExternalSourcePath(externalSourcePath);
        TimelineOffset = timelineOffset;
    }

    public string? ExternalSourcePath { get; }

    public bool IsExternal => ExternalSourcePath is not null;

    public MediaTime TimelineOffset { get; }

    public int StreamIndex { get; }

    public double GainDb { get; }

    public bool IsMuted { get; }

    private static string ValidateExternalSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("An external preview audio path must be absolute.", nameof(sourcePath));
        }

        return Path.GetFullPath(sourcePath);
    }
}
