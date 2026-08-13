using ClipEdit.Domain.Geometry;

namespace ClipEdit.Media.Export;

/// <summary>
/// User-controlled encoding choices that apply independently of the selected
/// container/codec preset.
/// </summary>
public sealed record ExportEncodingSettings
{
    public const int DefaultQuality = 75;
    public const int DefaultScalePercent = 100;
    public const int DefaultGifFrameRate = 15;

    public static ExportEncodingSettings Default { get; } = new();

    public ExportEncodingSettings(
        int quality = DefaultQuality,
        int scalePercent = DefaultScalePercent,
        int gifFrameRate = DefaultGifFrameRate)
    {
        if (quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }
        if (scalePercent is < 10 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(scalePercent));
        }
        if (gifFrameRate is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(gifFrameRate));
        }

        Quality = quality;
        ScalePercent = scalePercent;
        GifFrameRate = gifFrameRate;
    }

    public int Quality { get; }

    public int ScalePercent { get; }

    public int GifFrameRate { get; }

    public PixelSize CalculateOutputSize(PixelSize cropSize, bool requiresEvenDimensions)
    {
        if (ScalePercent == 100)
        {
            return cropSize;
        }

        var width = Math.Max(1, (int)((long)cropSize.Width * ScalePercent / 100));
        var height = Math.Max(1, (int)((long)cropSize.Height * ScalePercent / 100));
        if (requiresEvenDimensions)
        {
            width = Math.Max(2, width & ~1);
            height = Math.Max(2, height & ~1);
        }

        return new PixelSize(width, height);
    }
}
