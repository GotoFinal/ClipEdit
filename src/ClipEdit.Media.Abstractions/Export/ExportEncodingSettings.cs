using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Export;

public enum ExportQualityMode
{
    Custom,
    MatchSource,
}

public enum ExportEncodingSpeed
{
    Balanced,
    Faster,
    SmallerFile,
}

public enum ExportHardwareAcceleration
{
    Software,
    Automatic,
    Vulkan,
}

/// <summary>
/// User-controlled encoding choices that apply independently of the selected
/// container/codec preset.
/// </summary>
public sealed record ExportEncodingSettings
{
    public const int DefaultQuality = 75;
    public const int DefaultScalePercent = 100;
    public const int DefaultGifFrameRate = 15;
    public const int MinimumPlaybackSpeedPercent = 1;
    public const int MaximumPlaybackSpeedPercent = 10_000;
    public const int DefaultPlaybackSpeedPercent = 100;
    public const ExportQualityMode DefaultQualityMode = ExportQualityMode.MatchSource;
    public const ExportEncodingSpeed DefaultEncodingSpeed = ExportEncodingSpeed.Balanced;
    public const ExportHardwareAcceleration DefaultHardwareAcceleration = ExportHardwareAcceleration.Software;

    public static ExportEncodingSettings Default { get; } = new();

    public ExportEncodingSettings(
        int quality = DefaultQuality,
        int scalePercent = DefaultScalePercent,
        int gifFrameRate = DefaultGifFrameRate,
        int playbackSpeedPercent = DefaultPlaybackSpeedPercent,
        ExportQualityMode qualityMode = DefaultQualityMode,
        ExportEncodingSpeed encodingSpeed = DefaultEncodingSpeed,
        ExportHardwareAcceleration hardwareAcceleration = DefaultHardwareAcceleration)
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
        if (playbackSpeedPercent is < MinimumPlaybackSpeedPercent or > MaximumPlaybackSpeedPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(playbackSpeedPercent));
        }
        if (!Enum.IsDefined(qualityMode))
        {
            throw new ArgumentOutOfRangeException(nameof(qualityMode));
        }
        if (!Enum.IsDefined(encodingSpeed))
        {
            throw new ArgumentOutOfRangeException(nameof(encodingSpeed));
        }
        if (!Enum.IsDefined(hardwareAcceleration))
        {
            throw new ArgumentOutOfRangeException(nameof(hardwareAcceleration));
        }

        Quality = quality;
        ScalePercent = scalePercent;
        GifFrameRate = gifFrameRate;
        PlaybackSpeedPercent = playbackSpeedPercent;
        QualityMode = qualityMode;
        EncodingSpeed = encodingSpeed;
        HardwareAcceleration = hardwareAcceleration;
    }

    public int Quality { get; }

    public int ScalePercent { get; }

    public int GifFrameRate { get; }

    public int PlaybackSpeedPercent { get; }

    public ExportQualityMode QualityMode { get; }

    public ExportEncodingSpeed EncodingSpeed { get; }

    public ExportHardwareAcceleration HardwareAcceleration { get; }

    public double PlaybackSpeed => PlaybackSpeedPercent / 100d;

    public MediaTime ApplyPlaybackSpeed(MediaTime duration) =>
        duration * 100 / PlaybackSpeedPercent;

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
