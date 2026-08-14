namespace ClipEdit.Media.Export;

/// <summary>
/// Source-frame color properties needed to make an explicit export decision.
/// Values use FFmpeg/ffprobe names and are never inserted into a command without
/// first being mapped to a known supported value by the FFmpeg adapter.
/// </summary>
public sealed record ExportVideoColorInfo
{
    public ExportVideoColorInfo(
        string? pixelFormat,
        string? colorRange,
        string? colorSpace,
        string? colorTransfer,
        string? colorPrimaries)
    {
        PixelFormat = Normalize(pixelFormat);
        ColorRange = Normalize(colorRange);
        ColorSpace = Normalize(colorSpace);
        ColorTransfer = Normalize(colorTransfer);
        ColorPrimaries = Normalize(colorPrimaries);
    }

    public string? PixelFormat { get; }

    public string? ColorRange { get; }

    public string? ColorSpace { get; }

    public string? ColorTransfer { get; }

    public string? ColorPrimaries { get; }

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    public bool CanPreserveHdr =>
        IsHdr &&
        ColorRange is "tv" or "mpeg" or "limited" or "pc" or "jpeg" or "full" &&
        ColorSpace is "bt2020nc" or "bt2020c" or "ictcp" &&
        ColorPrimaries is "bt2020" or "smpte432";

    public bool IsCompatibleHdr(ExportVideoColorInfo? other) =>
        CanPreserveHdr &&
        other?.CanPreserveHdr == true &&
        ColorRange == other.ColorRange &&
        ColorSpace == other.ColorSpace &&
        ColorTransfer == other.ColorTransfer &&
        ColorPrimaries == other.ColorPrimaries;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
