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
                ExportDestinationMode.FileAndClipboard,
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
                        18,
                        7_500,
                        ExportQualityMode.Custom),
                ],
                10_000,
                true,
                ExportQualityMode.Custom);

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
            ExportDestination = ExportDestinationMode.Clipboard,
            PlaybackSpeedPercent = 175,
            RememberAdjustments = true,
            QualityMode = ExportQualityMode.Custom,
        };

        viewModel.ApplyExportPreferences(preferences);

        Assert.Equal(BuiltInExportPresets.Custom, viewModel.SelectedExportPreset);
        Assert.Equal(52, viewModel.ExportScalePercent);
        Assert.Equal(61, viewModel.ExportQuality);
        Assert.Equal(ExportQualityMode.Custom, viewModel.ExportQualityMode);
        Assert.True(viewModel.RememberExportAdjustments);
        Assert.Same(ExportContainerChoice.Matroska, viewModel.CustomExportContainer);
        Assert.Same(VideoCodecChoice.Vp9, viewModel.CustomVideoCodec);
        Assert.Same(AudioCodecChoice.None, viewModel.CustomAudioCodec);
        Assert.False(viewModel.CustomUseSourceFrameRate);
        Assert.Equal(50, viewModel.CustomFrameRate);
        Assert.Equal(ExportDestinationMode.Clipboard, viewModel.ExportDestination);
        Assert.Equal(175, viewModel.ExportPlaybackSpeedPercent);
        Assert.Equal("Copy", viewModel.ExportActionText);
        Assert.False(viewModel.IsProjectDirty);
    }

    [Fact]
    public void Export_adjustments_reset_when_remembering_is_disabled()
    {
        using var viewModel = new MainWindowViewModel(new StubMediaProbe());
        var preferences = ExportPreferences.Default with
        {
            ScalePercent = 52,
            Quality = 61,
            PlaybackSpeedPercent = 175,
            QualityMode = ExportQualityMode.Custom,
            RememberAdjustments = false,
        };

        viewModel.ApplyExportPreferences(preferences);

        Assert.False(viewModel.RememberExportAdjustments);
        Assert.Equal(100, viewModel.ExportScalePercent);
        Assert.Equal(75, viewModel.ExportQuality);
        Assert.Equal(100, viewModel.ExportPlaybackSpeedPercent);
        Assert.Equal(ExportQualityMode.MatchSource, viewModel.ExportQualityMode);
    }

    [Fact]
    public void Unremembered_preferences_do_not_write_transient_adjustments()
    {
        using var viewModel = new MainWindowViewModel(new StubMediaProbe())
        {
            ExportScalePercent = 40,
            ExportQuality = 25,
            ExportPlaybackSpeedPercent = 250,
            SelectedExportQuality = ExportQualityChoice.Custom,
        };

        var preferences = viewModel.CreateExportPreferences();

        Assert.False(preferences.RememberAdjustments);
        Assert.Equal(100, preferences.ScalePercent);
        Assert.Equal(75, preferences.Quality);
        Assert.Equal(100, preferences.PlaybackSpeedPercent);
        Assert.Equal(ExportQualityMode.MatchSource, preferences.QualityMode);
    }

    [Fact]
    public void Export_destination_choices_cover_file_clipboard_and_combined_output()
    {
        Assert.Equal(
            [
                ExportDestinationMode.File,
                ExportDestinationMode.Clipboard,
                ExportDestinationMode.FileAndClipboard,
            ],
            ExportDestinationChoice.All.Select(choice => choice.Value));
    }

    private sealed class StubMediaProbe : ClipEdit.Media.Probe.IMediaProbe
    {
        public Task<ClipEdit.Media.Probe.MediaProbeResult> ProbeAsync(
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
