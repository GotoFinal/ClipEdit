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
    TimeSpan? ProbeElapsed = null);

public sealed record ExportHardwareCapabilities(
    IReadOnlyList<ExportVideoEncoderCapability> H264Encoders)
{
    public ExportVideoEncoderCapability Get(ExportVideoEncoder encoder) =>
        H264Encoders.FirstOrDefault(capability => capability.Encoder == encoder) ??
        new ExportVideoEncoderCapability(
            encoder,
            encoder.ToString(),
            false,
            "This encoder was not probed.");

    public ExportVideoEncoderCapability FastestAvailableH264Encoder =>
        H264Encoders
            .Where(capability => capability.IsAvailable)
            .OrderBy(capability => capability.ProbeElapsed ?? TimeSpan.MaxValue)
            .ThenBy(capability => capability.Encoder == ExportVideoEncoder.Software ? 1 : 0)
            .FirstOrDefault() ??
        Get(ExportVideoEncoder.Software);
}

public interface IExportHardwareCapabilityProbe
{
    Task<ExportHardwareCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default);
}
