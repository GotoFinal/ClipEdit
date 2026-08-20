namespace ClipEdit.Media.Export;

public enum ExportVideoEncoder
{
    Software,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    Vaapi,
}

public sealed record ExportVideoEncoderCapability(
    ExportVideoEncoder Encoder,
    string DisplayName,
    bool IsAvailable,
    string Details);

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
}

public interface IExportHardwareCapabilityProbe
{
    Task<ExportHardwareCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default);
}
