using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Media.Export;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class HardwareExportViewModelTests
{
    [Fact]
    public void Successful_probe_exposes_only_validated_hardware_selection()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        viewModel.ConfigureExportHardwareCapabilityProbe(new StubProbe(
            new ExportHardwareCapabilities(
            [
                new(ExportVideoEncoder.Software, "Software (x264)", true, "Available"),
                new(ExportVideoEncoder.NvidiaNvenc, "NVIDIA NVENC", true, "Available · h264_nvenc"),
                new(ExportVideoEncoder.IntelQuickSync, "Intel Quick Sync", false, "Unavailable · no device"),
            ])));

        var nvenc = viewModel.ExportVideoEncoderChoices.Single(choice =>
            choice.Value == ExportVideoEncoder.NvidiaNvenc);
        viewModel.SelectedExportVideoEncoder = nvenc;

        Assert.Equal(ExportVideoEncoder.NvidiaNvenc, viewModel.PreferredExportVideoEncoder);
        Assert.Contains("h264_nvenc", viewModel.ExportVideoEncoderStatus);
        Assert.False(viewModel.IsExportHardwareProbeRunning);
    }

    [Fact]
    public void Failed_hardware_choice_is_rejected_with_diagnostic_feedback()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        viewModel.ConfigureExportHardwareCapabilityProbe(new StubProbe(
            new ExportHardwareCapabilities(
            [
                new(ExportVideoEncoder.Software, "Software (x264)", true, "Available"),
                new(ExportVideoEncoder.AmdAmf, "AMD AMF", false, "Unavailable · no AMF device"),
            ])));

        var unavailable = viewModel.ExportVideoEncoderChoices.Single(choice =>
            choice.Value == ExportVideoEncoder.AmdAmf);
        viewModel.SelectedExportVideoEncoder = unavailable;

        Assert.Equal(ExportVideoEncoder.Automatic, viewModel.PreferredExportVideoEncoder);
        Assert.Equal(ExportVideoEncoder.Software, viewModel.EffectiveExportVideoEncoder);
        Assert.Contains("no AMF device", viewModel.StatusText);
    }

    [Fact]
    public void Automatic_choice_uses_the_fastest_successful_timed_probe()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        viewModel.ConfigureExportHardwareCapabilityProbe(new StubProbe(
            new ExportHardwareCapabilities(
            [
                new(
                    ExportVideoEncoder.Software,
                    "Software (x264)",
                    true,
                    "Available",
                    TimeSpan.FromSeconds(0.8)),
                new(
                    ExportVideoEncoder.NvidiaNvenc,
                    "NVIDIA NVENC",
                    true,
                    "Available",
                    TimeSpan.FromSeconds(0.2)),
            ])));

        var automatic = viewModel.ExportVideoEncoderChoices.Single(choice =>
            choice.Value == ExportVideoEncoder.Automatic);
        viewModel.SelectedExportVideoEncoder = automatic;

        Assert.Equal(ExportVideoEncoder.Automatic, viewModel.PreferredExportVideoEncoder);
        Assert.Equal(ExportVideoEncoder.NvidiaNvenc, viewModel.EffectiveExportVideoEncoder);
        Assert.Contains("NVIDIA NVENC", viewModel.ExportVideoEncoderStatus);
    }

    [Fact]
    public void Changing_custom_codec_reprobes_and_preserves_the_preferred_backend()
    {
        var probe = new CodecProbe(new Dictionary<VideoCodecFamily, ExportHardwareCapabilities>
        {
            [VideoCodecFamily.H264] = Capabilities(
                VideoCodecFamily.H264,
                ExportVideoEncoder.NvidiaNvenc),
            [VideoCodecFamily.Hevc] = Capabilities(
                VideoCodecFamily.Hevc,
                ExportVideoEncoder.NvidiaNvenc),
            [VideoCodecFamily.Vp9] = Capabilities(
                VideoCodecFamily.Vp9,
                ExportVideoEncoder.IntelQuickSync),
        });
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        viewModel.ConfigureExportHardwareCapabilityProbe(probe);
        viewModel.SelectedExportVideoEncoder = viewModel.ExportVideoEncoderChoices.Single(choice =>
            choice.Value == ExportVideoEncoder.NvidiaNvenc);

        viewModel.SelectedExportPreset = BuiltInExportPresets.Custom;
        viewModel.CustomExportContainer = ExportContainerChoice.Matroska;
        viewModel.CustomVideoCodec = VideoCodecChoice.Hevc;

        Assert.Equal(ExportVideoEncoder.NvidiaNvenc, viewModel.EffectiveExportVideoEncoder);
        Assert.Contains(VideoCodecFamily.Hevc, probe.RequestedCodecs);

        viewModel.CustomVideoCodec = VideoCodecChoice.Vp9;

        Assert.Equal(ExportVideoEncoder.NvidiaNvenc, viewModel.PreferredExportVideoEncoder);
        Assert.Equal(ExportVideoEncoder.Software, viewModel.EffectiveExportVideoEncoder);
        Assert.False(viewModel.ExportVideoEncoderChoices.Single(choice =>
            choice.Value == ExportVideoEncoder.NvidiaNvenc).IsAvailable);

        viewModel.CustomVideoCodec = VideoCodecChoice.Hevc;

        Assert.Equal(ExportVideoEncoder.NvidiaNvenc, viewModel.EffectiveExportVideoEncoder);
    }

    [Fact]
    public void Codec_probe_updates_keep_the_choice_collection_and_items_stable()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var originalChoices = viewModel.ExportVideoEncoderChoices;
        var originalItems = originalChoices.ToArray();

        viewModel.ConfigureExportHardwareCapabilityProbe(new StubProbe(
            Capabilities(VideoCodecFamily.H264, ExportVideoEncoder.NvidiaNvenc)));

        Assert.Same(originalChoices, viewModel.ExportVideoEncoderChoices);
        Assert.Equal(originalItems.Length, viewModel.ExportVideoEncoderChoices.Count);
        for (var index = 0; index < originalItems.Length; index++)
        {
            Assert.Same(originalItems[index], viewModel.ExportVideoEncoderChoices[index]);
        }
        Assert.Contains(
            viewModel.SelectedExportVideoEncoder,
            viewModel.ExportVideoEncoderChoices);
    }

    [Fact]
    public void Changing_preferred_gpu_reprobes_the_same_codec_for_that_device()
    {
        var probe = new CodecProbe(new Dictionary<VideoCodecFamily, ExportHardwareCapabilities>
        {
            [VideoCodecFamily.H264] = Capabilities(
                VideoCodecFamily.H264,
                ExportVideoEncoder.NvidiaNvenc),
        });
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        viewModel.ConfigureExportHardwareCapabilityProbe(probe);

        viewModel.SelectedExportGpu = ExportGpuChoice.FromValue(3);

        Assert.Equal([null, 3], probe.RequestedDeviceIndices);
        Assert.Equal(3, viewModel.PreferredHardwareDeviceIndex);
        Assert.Contains("NVENC, QSV and VA-API", viewModel.ExportGpuDescription);
    }

    [Fact]
    public async Task Hardware_settings_replace_numeric_gpu_choices_with_detected_names()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null)
        {
            SelectedExportGpu = ExportGpuChoice.FromValue(0),
        };
        viewModel.ConfigureExportHardwareDeviceProbe(new DeviceProbe(
        [
            new ExportHardwareDevice(0, "NVIDIA GeForce RTX 5090"),
            new ExportHardwareDevice(1, "Intel Arc Graphics"),
        ]));

        await viewModel.RefreshExportGpuDevicesAsync();

        Assert.Equal(
            ["Auto", "NVIDIA GeForce RTX 5090 (GPU 0)", "Intel Arc Graphics (GPU 1)"],
            viewModel.ExportGpuChoices.Select(choice => choice.MenuText));
        Assert.Equal(0, viewModel.PreferredHardwareDeviceIndex);
        Assert.Equal("NVIDIA GeForce RTX 5090", viewModel.SelectedExportGpu.DisplayName);
        Assert.False(viewModel.HasExportGpuProbeStatus);
    }

    private static ExportHardwareCapabilities Capabilities(
        VideoCodecFamily videoCodec,
        ExportVideoEncoder hardwareEncoder) =>
        new(
        [
            new(
                ExportVideoEncoder.Software,
                "Software",
                true,
                "Available",
                VideoCodec: videoCodec),
            new(
                hardwareEncoder,
                hardwareEncoder.ToString(),
                true,
                "Available",
                TimeSpan.FromSeconds(0.2),
                videoCodec),
        ]);

    private sealed class StubProbe(ExportHardwareCapabilities capabilities)
        : IExportHardwareCapabilityProbe
    {
        public Task<ExportHardwareCapabilities> ProbeAsync(
            VideoCodecFamily videoCodec,
            int? hardwareDeviceIndex = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);
    }

    private sealed class CodecProbe(
        IReadOnlyDictionary<VideoCodecFamily, ExportHardwareCapabilities> capabilities)
        : IExportHardwareCapabilityProbe
    {
        public List<VideoCodecFamily> RequestedCodecs { get; } = [];

        public List<int?> RequestedDeviceIndices { get; } = [];

        public Task<ExportHardwareCapabilities> ProbeAsync(
            VideoCodecFamily videoCodec,
            int? hardwareDeviceIndex = null,
            CancellationToken cancellationToken = default)
        {
            RequestedCodecs.Add(videoCodec);
            RequestedDeviceIndices.Add(hardwareDeviceIndex);
            return Task.FromResult(capabilities[videoCodec]);
        }
    }

    private sealed class DeviceProbe(IReadOnlyList<ExportHardwareDevice> devices)
        : IExportHardwareDeviceProbe
    {
        public Task<IReadOnlyList<ExportHardwareDevice>> ProbeHardwareDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(devices);
    }
}
