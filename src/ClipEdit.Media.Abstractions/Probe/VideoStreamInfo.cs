using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public sealed record VideoStreamInfo : MediaStreamInfo
{
    public VideoStreamInfo(
        int index,
        string codecName,
        string? codecLongName,
        string? profile,
        string? language,
        string? title,
        bool isDefault,
        bool isForced,
        MediaTime? timeBase,
        MediaTime? startTime,
        MediaTime? duration,
        PixelSize encodedSize,
        int rotationDegrees,
        FrameRate? nominalFrameRate,
        FrameRate? averageFrameRate,
        string? pixelFormat,
        string? sampleAspectRatio,
        string? displayAspectRatio,
        string? colorRange,
        string? colorSpace,
        string? colorTransfer,
        string? colorPrimaries,
        string? fieldOrder,
        long? bitRateBitsPerSecond = null,
        string? codecTag = null,
        int? codecLevel = null,
        string? codecExtradataHash = null)
        : base(
            index,
            MediaStreamKind.Video,
            codecName,
            codecLongName,
            profile,
            language,
            title,
            isDefault,
            isForced,
            timeBase,
            startTime,
            duration)
    {
        if (bitRateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitRateBitsPerSecond));
        }
        EncodedSize = encodedSize;
        RotationDegrees = NormalizeRotation(rotationDegrees);
        NominalFrameRate = nominalFrameRate;
        AverageFrameRate = averageFrameRate;
        PixelFormat = pixelFormat;
        SampleAspectRatio = sampleAspectRatio;
        DisplayAspectRatio = displayAspectRatio;
        ColorRange = colorRange;
        ColorSpace = colorSpace;
        ColorTransfer = colorTransfer;
        ColorPrimaries = colorPrimaries;
        FieldOrder = fieldOrder;
        BitRateBitsPerSecond = bitRateBitsPerSecond;
        CodecTag = codecTag;
        CodecLevel = codecLevel;
        CodecExtradataHash = codecExtradataHash;
    }

    public PixelSize EncodedSize { get; }

    public PixelSize OrientedSize =>
        RotationDegrees is 90 or 270
            ? new PixelSize(EncodedSize.Height, EncodedSize.Width)
            : EncodedSize;

    public int RotationDegrees { get; }

    public FrameRate? NominalFrameRate { get; }

    public FrameRate? AverageFrameRate { get; }

    public string? PixelFormat { get; }

    public string? SampleAspectRatio { get; }

    public string? DisplayAspectRatio { get; }

    public string? ColorRange { get; }

    public string? ColorSpace { get; }

    public string? ColorTransfer { get; }

    public string? ColorPrimaries { get; }

    public string? FieldOrder { get; }

    public long? BitRateBitsPerSecond { get; }

    public string? CodecTag { get; }

    public int? CodecLevel { get; }

    public string? CodecExtradataHash { get; }

    private static int NormalizeRotation(int value)
    {
        var normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }
}
