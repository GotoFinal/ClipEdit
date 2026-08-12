using ClipEdit.Media.Export;

namespace ClipEdit.Application.Export;

public static class BuiltInExportPresets
{
    public static ExportPreset Mp4Compatible { get; } = new(
        "mp4-compatible-v1",
        "MP4 — compatible",
        ".mp4",
        ExportContainer.Mp4,
        VideoCodecFamily.H264,
        AudioCodecFamily.Aac,
        requiresEvenDimensions: true);

    public static ExportPreset WebM { get; } = new(
        "webm-v1",
        "WebM",
        ".webm",
        ExportContainer.WebM,
        VideoCodecFamily.Vp9,
        AudioCodecFamily.Opus,
        requiresEvenDimensions: true);

    public static IReadOnlyList<ExportPreset> All { get; } = [Mp4Compatible, WebM];
}
