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

    public int StreamIndex { get; }

    public double GainDb { get; }

    public bool IsMuted { get; }
}
