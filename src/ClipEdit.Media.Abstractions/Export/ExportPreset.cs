namespace ClipEdit.Media.Export;

public enum ExportContainer
{
    Mp4,
    WebM,
}

public enum VideoCodecFamily
{
    H264,
    Vp9,
}

public enum AudioCodecFamily
{
    Aac,
    Opus,
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
        bool requiresEvenDimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);

        Id = id;
        DisplayName = displayName;
        FileExtension = fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}";
        Container = container;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        RequiresEvenDimensions = requiresEvenDimensions;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string FileExtension { get; }

    public ExportContainer Container { get; }

    public VideoCodecFamily VideoCodec { get; }

    public AudioCodecFamily AudioCodec { get; }

    public bool RequiresEvenDimensions { get; }
}
