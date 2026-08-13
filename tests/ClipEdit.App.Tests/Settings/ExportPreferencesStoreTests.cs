using ClipEdit.App.Settings;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Media.Export;

namespace ClipEdit.App.Tests.Settings;

public sealed class ExportPreferencesStoreTests
{
    [Fact]
    public void Last_used_values_and_named_presets_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-export-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "export-settings.json");
        try
        {
            var store = new ExportPreferencesStore(path);
            var settings = new ExportPreferences(
                BuiltInExportPresets.Custom.Id,
                43,
                62,
                18,
                ExportContainer.Matroska,
                VideoCodecFamily.Vp9,
                AudioCodecFamily.Opus,
                false,
                48,
                [
                    new SavedExportPresetViewModel(
                        "VP9 preset",
                        ExportContainer.Matroska,
                        VideoCodecFamily.Vp9,
                        AudioCodecFamily.Opus,
                        false,
                        48,
                        43,
                        62,
                        18),
                ]);

            Assert.True(store.Save(settings));
            var restored = store.Load();

            Assert.Equivalent(settings, restored, strict: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Invalid_codec_combinations_are_normalized()
    {
        var settings = ExportPreferences.Default with
        {
            CustomContainer = ExportContainer.WebM,
            CustomVideoCodec = VideoCodecFamily.H264,
            CustomAudioCodec = AudioCodecFamily.Aac,
        };

        var normalized = settings.Normalize();

        Assert.Equal(VideoCodecFamily.Vp9, normalized.CustomVideoCodec);
        Assert.Equal(AudioCodecFamily.Opus, normalized.CustomAudioCodec);
    }

    [Fact]
    public void View_model_applies_the_last_used_export_configuration()
    {
        using var viewModel = new MainWindowViewModel(new StubMediaProbe());
        var preferences = ExportPreferences.Default with
        {
            SelectedExportPresetId = BuiltInExportPresets.Custom.Id,
            ScalePercent = 52,
            Quality = 61,
            CustomContainer = ExportContainer.Matroska,
            CustomVideoCodec = VideoCodecFamily.Vp9,
            CustomAudioCodec = AudioCodecFamily.None,
            CustomUseSourceFrameRate = false,
            CustomFrameRate = 50,
        };

        viewModel.ApplyExportPreferences(preferences);

        Assert.Equal(BuiltInExportPresets.Custom, viewModel.SelectedExportPreset);
        Assert.Equal(52, viewModel.ExportScalePercent);
        Assert.Equal(61, viewModel.ExportQuality);
        Assert.Same(ExportContainerChoice.Matroska, viewModel.CustomExportContainer);
        Assert.Same(VideoCodecChoice.Vp9, viewModel.CustomVideoCodec);
        Assert.Same(AudioCodecChoice.None, viewModel.CustomAudioCodec);
        Assert.False(viewModel.CustomUseSourceFrameRate);
        Assert.Equal(50, viewModel.CustomFrameRate);
        Assert.False(viewModel.IsProjectDirty);
    }

    private sealed class StubMediaProbe : ClipEdit.Media.Probe.IMediaProbe
    {
        public Task<ClipEdit.Media.Probe.MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
