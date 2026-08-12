namespace ClipEdit.Media.Frames;

public sealed record DecodedFrame
{
    public DecodedFrame(ReadOnlyMemory<byte> encodedImage, string mediaType)
    {
        if (encodedImage.IsEmpty)
        {
            throw new ArgumentException("A decoded frame image cannot be empty.", nameof(encodedImage));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        EncodedImage = encodedImage;
        MediaType = mediaType;
    }

    public ReadOnlyMemory<byte> EncodedImage { get; }

    public string MediaType { get; }
}
