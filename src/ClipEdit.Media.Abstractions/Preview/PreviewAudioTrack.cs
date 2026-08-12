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
        : this(streamIndex, gainDb, isMuted)
    {
        ExternalSourcePath = ValidateExternalSourcePath(externalSourcePath);
    }

    public string? ExternalSourcePath { get; }

    public bool IsExternal => ExternalSourcePath is not null;

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
