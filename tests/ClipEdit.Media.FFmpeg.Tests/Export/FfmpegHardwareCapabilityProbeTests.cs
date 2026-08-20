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
    public void Codec_probes_cover_only_supported_backend_pairs()
    {
        Assert.Equal(
            ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_vaapi"],
            FfmpegHardwareCapabilityProbe.CreateProbes(VideoCodecFamily.Hevc)
                .Select(probe => probe.FfmpegEncoderName));
        Assert.Equal(
            ["libvpx", "vp8_vaapi"],
            FfmpegHardwareCapabilityProbe.CreateProbes(VideoCodecFamily.Vp8)
                .Select(probe => probe.FfmpegEncoderName));
        Assert.Equal(
            ["libvpx-vp9", "vp9_qsv", "vp9_vaapi"],
            FfmpegHardwareCapabilityProbe.CreateProbes(VideoCodecFamily.Vp9)
                .Select(probe => probe.FfmpegEncoderName));
        Assert.Equal(
            ["libaom-av1", "av1_nvenc", "av1_qsv", "av1_amf", "av1_vaapi"],
            FfmpegHardwareCapabilityProbe.CreateProbes(VideoCodecFamily.Av1)
                .Select(probe => probe.FfmpegEncoderName));
        Assert.All(
            FfmpegHardwareCapabilityProbe.CreateProbes(VideoCodecFamily.Av1),
            probe => Assert.Equal(VideoCodecFamily.Av1, probe.VideoCodec));
    }

    [Fact]
    public void Preferred_gpu_is_used_by_backend_capability_tests_when_supported()
    {
        var probes = FfmpegHardwareCapabilityProbe.CreateH264Probes(2);

        var nvenc = probes.Single(probe => probe.Encoder == ExportVideoEncoder.NvidiaNvenc);
        var qsv = probes.Single(probe => probe.Encoder == ExportVideoEncoder.IntelQuickSync);
        var amf = probes.Single(probe => probe.Encoder == ExportVideoEncoder.AmdAmf);
        var vaapi = probes.Single(probe => probe.Encoder == ExportVideoEncoder.Vaapi);

        Assert.Equal("2", ValueAfter(nvenc.Arguments, "-gpu"));
        Assert.Equal("2", ValueAfter(qsv.Arguments, "-qsv_device"));
        Assert.DoesNotContain("-gpu", amf.Arguments);
        Assert.Equal("vaapi=clipeditva:2", ValueAfter(vaapi.Arguments, "-init_hw_device"));
    }

    [Fact]
    public async Task Probe_results_are_cached_for_the_same_executable_fingerprint()
    {
        var firstProbe = new FfmpegHardwareCapabilityProbe(Environment.ProcessPath!);
        var secondProbe = new FfmpegHardwareCapabilityProbe(Environment.ProcessPath!);

        var first = await firstProbe.ProbeAsync(VideoCodecFamily.H264);
        var second = await secondProbe.ProbeAsync(VideoCodecFamily.H264);

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

            var capabilities = await probe.ProbeAsync(VideoCodecFamily.H264);

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

    [Fact]
    public void Capability_selection_is_scoped_to_the_requested_codec()
    {
        var capabilities = new ExportHardwareCapabilities(
        [
            new(ExportVideoEncoder.Software, "Software (x264)", true, "Available"),
            new(
                ExportVideoEncoder.NvidiaNvenc,
                "NVIDIA NVENC",
                true,
                "Available",
                TimeSpan.FromSeconds(0.2)),
            new(
                ExportVideoEncoder.Software,
                "Software (x265)",
                true,
                "Available",
                TimeSpan.FromSeconds(0.8),
                VideoCodecFamily.Hevc),
        ]);

        Assert.Equal(
            ExportVideoEncoder.NvidiaNvenc,
            capabilities.FastestAvailable(VideoCodecFamily.H264).Encoder);
        Assert.Equal(
            ExportVideoEncoder.Software,
            capabilities.FastestAvailable(VideoCodecFamily.Hevc).Encoder);
        Assert.False(capabilities.Get(ExportVideoEncoder.NvidiaNvenc, VideoCodecFamily.Hevc).IsAvailable);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing {option}");
        return arguments[index + 1];
    }
}
