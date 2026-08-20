using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegHardwareCapabilityProbeTests
{
    [Fact]
    public void H264_probes_execute_timed_encode_workloads_for_each_backend()
    {
        var probes = FfmpegHardwareCapabilityProbe.CreateH264Probes();

        Assert.Equal(
            [
                ExportVideoEncoder.NvidiaNvenc,
                ExportVideoEncoder.IntelQuickSync,
                ExportVideoEncoder.AmdAmf,
                ExportVideoEncoder.Vaapi,
            ],
            probes.Select(probe => probe.Encoder));
        Assert.All(probes, probe =>
        {
            Assert.Contains("-frames:v", probe.Arguments);
            Assert.Contains("120", probe.Arguments);
            Assert.Contains(probe.FfmpegEncoderName, probe.Arguments);
            Assert.Equal("-", probe.Arguments[^1]);
        });
        var vaapi = probes.Single(probe => probe.Encoder == ExportVideoEncoder.Vaapi);
        Assert.Contains("-init_hw_device", vaapi.Arguments);
        Assert.Contains("format=nv12,hwupload", vaapi.Arguments);
    }

    [Fact]
    public async Task Probe_results_are_cached_for_the_same_executable_fingerprint()
    {
        var firstProbe = new FfmpegHardwareCapabilityProbe(Environment.ProcessPath!);
        var secondProbe = new FfmpegHardwareCapabilityProbe(Environment.ProcessPath!);

        var first = await firstProbe.ProbeAsync();
        var second = await secondProbe.ProbeAsync();

        Assert.Same(first, second);
        Assert.False(first.Get(ExportVideoEncoder.Software).IsAvailable);
        Assert.False(first.Get(ExportVideoEncoder.NvidiaNvenc).IsAvailable);
    }

    [Fact]
    public async Task Invalid_executable_format_is_reported_as_unavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-invalid-ffmpeg-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(path, []);
            var probe = new FfmpegHardwareCapabilityProbe(path);

            var capabilities = await probe.ProbeAsync();

            Assert.All(capabilities.H264Encoders, capability => Assert.False(capability.IsAvailable));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fastest_available_encoder_uses_measured_elapsed_time()
    {
        var capabilities = new ExportHardwareCapabilities(
        [
            new(ExportVideoEncoder.Software, "Software", true, "Available", TimeSpan.FromSeconds(1)),
            new(ExportVideoEncoder.NvidiaNvenc, "NVENC", true, "Available", TimeSpan.FromSeconds(0.2)),
            new(ExportVideoEncoder.AmdAmf, "AMF", false, "Unavailable"),
        ]);

        Assert.Equal(
            ExportVideoEncoder.NvidiaNvenc,
            capabilities.FastestAvailableH264Encoder.Encoder);
    }
}
