namespace ClipEdit.Media.Export;

public enum ExportVideoEncoder
{
    Software,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    Vaapi,
    Automatic,
}

public sealed record ExportVideoEncoderCapability(
    ExportVideoEncoder Encoder,
    string DisplayName,
    bool IsAvailable,
    string Details,
    TimeSpan? ProbeElapsed = null,
    VideoCodecFamily VideoCodec = VideoCodecFamily.H264);

public sealed record ExportHardwareCapabilities(
    IReadOnlyList<ExportVideoEncoderCapability> EncoderCapabilities)
{
    public IReadOnlyList<ExportVideoEncoderCapability> H264Encoders =>
        EncoderCapabilities
            .Where(capability => capability.VideoCodec == VideoCodecFamily.H264)
            .ToArray();

    public ExportVideoEncoderCapability Get(
        ExportVideoEncoder encoder,
        VideoCodecFamily videoCodec = VideoCodecFamily.H264) =>
        EncoderCapabilities.FirstOrDefault(capability =>
            capability.Encoder == encoder && capability.VideoCodec == videoCodec) ??
        new ExportVideoEncoderCapability(
            encoder,
            encoder.ToString(),
            false,
            $"This backend does not provide a {VideoCodecLabel(videoCodec)} encoder.",
            VideoCodec: videoCodec);

    public ExportVideoEncoderCapability FastestAvailableH264Encoder =>
        FastestAvailable(VideoCodecFamily.H264);

    public ExportVideoEncoderCapability FastestAvailable(VideoCodecFamily videoCodec) =>
        EncoderCapabilities
            .Where(capability => capability.VideoCodec == videoCodec)
            .Where(capability => capability.IsAvailable)
            .OrderBy(capability => capability.ProbeElapsed ?? TimeSpan.MaxValue)
            .ThenBy(capability => capability.Encoder == ExportVideoEncoder.Software ? 1 : 0)
            .FirstOrDefault() ??
        Get(ExportVideoEncoder.Software, videoCodec);

    private static string VideoCodecLabel(VideoCodecFamily codec) => codec switch
    {
        VideoCodecFamily.H264 => "H.264",
        VideoCodecFamily.Hevc => "HEVC",
        VideoCodecFamily.Vp8 => "VP8",
        VideoCodecFamily.Vp9 => "VP9",
        VideoCodecFamily.Av1 => "AV1",
        VideoCodecFamily.Gif => "GIF",
        _ => codec.ToString(),
    };
}

public interface IExportHardwareCapabilityProbe
{
    Task<ExportHardwareCapabilities> ProbeAsync(
        VideoCodecFamily videoCodec,
        int? hardwareDeviceIndex = null,
        CancellationToken cancellationToken = default);
}
