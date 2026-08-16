using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Export;

public sealed record VideoStreamCopySignature
{
    public VideoStreamCopySignature(
        string codecName,
        string codecTag,
        string codecExtradataHash,
        PixelSize encodedSize,
        MediaTime timeBase,
        FrameRate averageFrameRate,
        string pixelFormat,
        string? profile,
        int? codecLevel,
        string? sampleAspectRatio,
        string? colorRange,
        string? colorSpace,
        string? colorTransfer,
        string? colorPrimaries,
        string? fieldOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codecName);
        ArgumentException.ThrowIfNullOrWhiteSpace(codecTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(codecExtradataHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelFormat);
        if (timeBase <= MediaTime.Zero || averageFrameRate.IsZero)
        {
            throw new ArgumentException("A packet-copy video signature requires valid timing.");
        }

        CodecName = codecName;
        CodecTag = codecTag;
        CodecExtradataHash = codecExtradataHash;
        EncodedSize = encodedSize;
        TimeBase = timeBase;
        AverageFrameRate = averageFrameRate;
        PixelFormat = pixelFormat;
        Profile = profile;
        CodecLevel = codecLevel;
        SampleAspectRatio = sampleAspectRatio;
        ColorRange = colorRange;
        ColorSpace = colorSpace;
        ColorTransfer = colorTransfer;
        ColorPrimaries = colorPrimaries;
        FieldOrder = fieldOrder;
    }

    public string CodecName { get; }
    public string CodecTag { get; }
    public string CodecExtradataHash { get; }
    public PixelSize EncodedSize { get; }
    public MediaTime TimeBase { get; }
    public FrameRate AverageFrameRate { get; }
    public string PixelFormat { get; }
    public string? Profile { get; }
    public int? CodecLevel { get; }
    public string? SampleAspectRatio { get; }
    public string? ColorRange { get; }
    public string? ColorSpace { get; }
    public string? ColorTransfer { get; }
    public string? ColorPrimaries { get; }
    public string? FieldOrder { get; }
}

public sealed record AudioStreamCopySignature
{
    public AudioStreamCopySignature(
        string codecName,
        string codecTag,
        string codecExtradataHash,
        MediaTime timeBase,
        int sampleRate,
        int channelCount,
        string channelLayout,
        string sampleFormat,
        string? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codecName);
        ArgumentException.ThrowIfNullOrWhiteSpace(codecTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(codecExtradataHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelLayout);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleFormat);
        if (timeBase <= MediaTime.Zero || sampleRate <= 0 || channelCount <= 0)
        {
            throw new ArgumentException("A packet-copy audio signature requires valid timing and layout.");
        }

        CodecName = codecName;
        CodecTag = codecTag;
        CodecExtradataHash = codecExtradataHash;
        TimeBase = timeBase;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        ChannelLayout = channelLayout;
        SampleFormat = sampleFormat;
        Profile = profile;
    }

    public string CodecName { get; }
    public string CodecTag { get; }
    public string CodecExtradataHash { get; }
    public MediaTime TimeBase { get; }
    public int SampleRate { get; }
    public int ChannelCount { get; }
    public string ChannelLayout { get; }
    public string SampleFormat { get; }
    public string? Profile { get; }
}

public sealed record SegmentStreamCopyInfo(
    VideoStreamCopySignature Video,
    AudioStreamCopySignature? Audio,
    bool StartsOnKeyframeOrAtSourceStart,
    bool EndsOnKeyframeOrAtSourceEnd);
