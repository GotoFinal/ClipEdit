namespace ClipEdit.Media.Analysis;

public sealed record WaveformImage
{
    public WaveformImage(ReadOnlyMemory<byte> encodedImage, string mediaType)
    {
        if (encodedImage.IsEmpty)
        {
            throw new ArgumentException("A rendered waveform image cannot be empty.", nameof(encodedImage));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        EncodedImage = encodedImage;
        MediaType = mediaType;
    }

    public ReadOnlyMemory<byte> EncodedImage { get; }

    public string MediaType { get; }
}
