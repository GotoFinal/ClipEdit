using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Export;

public enum ExportContainer
{
    Mp4,
    WebM,
    Matroska,
    Gif,
}

public enum VideoCodecFamily
{
    H264,
    Vp9,
    Gif,
}

public enum AudioCodecFamily
{
    None,
    Aac,
    Opus,
}

public enum ExportParameterMode
{
    Fixed,
    MatchInput,
}

/// <summary>
/// Product-level output intent. Adapter-specific encoder names are deliberately
/// excluded so the application does not construct FFmpeg commands.
/// </summary>
public sealed record ExportPreset
{
    public ExportPreset(
        string id,
        string displayName,
        string fileExtension,
        ExportContainer container,
        VideoCodecFamily videoCodec,
        AudioCodecFamily audioCodec,
        bool requiresEvenDimensions,
        ExportParameterMode parameterMode = ExportParameterMode.Fixed,
        FrameRate? frameRate = null,
        long? videoBitRateBitsPerSecond = null,
        long? audioBitRateBitsPerSecond = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);

        if (frameRate is { IsZero: true })
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }
        if (videoBitRateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(videoBitRateBitsPerSecond));
        }
        if (audioBitRateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBitRateBitsPerSecond));
        }

        Id = id;
        DisplayName = displayName;
        FileExtension = fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}";
        Container = container;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        RequiresEvenDimensions = requiresEvenDimensions;
        ParameterMode = parameterMode;
        FrameRate = frameRate;
        VideoBitRateBitsPerSecond = videoBitRateBitsPerSecond;
        AudioBitRateBitsPerSecond = audioBitRateBitsPerSecond;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string FileExtension { get; }

    public ExportContainer Container { get; }

    public VideoCodecFamily VideoCodec { get; }

    public AudioCodecFamily AudioCodec { get; }

    public bool RequiresEvenDimensions { get; }

    public ExportParameterMode ParameterMode { get; }

    public FrameRate? FrameRate { get; }

    public long? VideoBitRateBitsPerSecond { get; }

    public long? AudioBitRateBitsPerSecond { get; }

    public bool SupportsAudio => AudioCodec != AudioCodecFamily.None;
}
