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
    public void Codec_probe_updates_never_publish_a_selection_outside_the_new_choices()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var observedChoicesRefresh = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainWindowViewModel.ExportVideoEncoderChoices))
            {
                return;
            }

            observedChoicesRefresh = true;
            Assert.Contains(
                viewModel.SelectedExportVideoEncoder,
                viewModel.ExportVideoEncoderChoices);

            // Simulate the transient null write produced by a two-way ComboBox
            // while it replaces ItemsSource.
            var expectedSelection = viewModel.SelectedExportVideoEncoder;
            viewModel.SelectedExportVideoEncoder = null!;
            Assert.Same(expectedSelection, viewModel.SelectedExportVideoEncoder);
        };

        viewModel.ConfigureExportHardwareCapabilityProbe(new StubProbe(
            Capabilities(VideoCodecFamily.H264, ExportVideoEncoder.NvidiaNvenc)));

        Assert.True(observedChoicesRefresh);
        Assert.Contains(
            viewModel.SelectedExportVideoEncoder,
            viewModel.ExportVideoEncoderChoices);
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);
    }

    private sealed class CodecProbe(
        IReadOnlyDictionary<VideoCodecFamily, ExportHardwareCapabilities> capabilities)
        : IExportHardwareCapabilityProbe
    {
        public List<VideoCodecFamily> RequestedCodecs { get; } = [];

        public Task<ExportHardwareCapabilities> ProbeAsync(
            VideoCodecFamily videoCodec,
            CancellationToken cancellationToken = default)
        {
            RequestedCodecs.Add(videoCodec);
            return Task.FromResult(capabilities[videoCodec]);
        }
    }
}
