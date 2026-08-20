using ClipEdit.App.ViewModels;
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

        Assert.Equal(ExportVideoEncoder.Software, viewModel.PreferredExportVideoEncoder);
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

    private sealed class StubProbe(ExportHardwareCapabilities capabilities)
        : IExportHardwareCapabilityProbe
    {
        public Task<ExportHardwareCapabilities> ProbeAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);
    }
}
