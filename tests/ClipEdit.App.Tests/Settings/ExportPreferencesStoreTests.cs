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
                        ExportQualityMode.BitRate,
                        VideoBitRateKbps: 7_500),
                ],
                10_000,
                true,
                ExportQualityMode.Custom,
                ExportEncodingSpeed.Faster,
                ExportHardwareAcceleration.Vulkan,
                ExportVideoEncoder.NvidiaNvenc,
                8_500,
                2);

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
    public void Older_settings_without_performance_fields_keep_safe_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-old-export-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "export-settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                """
                {
                  "selectedExportPresetId": "mp4-compatible",
                  "scalePercent": 100,
                  "quality": 75,
                  "gifFrameRate": 15,
                  "customContainer": 0,
                  "customVideoCodec": 0,
                  "customAudioCodec": 1,
                  "customUseSourceFrameRate": true,
                  "customFrameRate": 30,
                  "exportDestination": 0,
                  "savedPresets": [],
                  "playbackSpeedPercent": 100,
                  "rememberAdjustments": false,
                  "qualityMode": 1
                }
                """);

            var restored = new ExportPreferencesStore(path).Load();

            Assert.Equal(ExportEncodingSpeed.Balanced, restored.EncodingSpeed);
            Assert.Equal(ExportHardwareAcceleration.Software, restored.HardwareAcceleration);
            Assert.Equal(ExportVideoEncoder.Automatic, restored.VideoEncoder);
            Assert.Null(restored.HardwareDeviceIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(ExportContainer.Mp4, AudioCodecFamily.Aac)]
    [InlineData(ExportContainer.WebM, AudioCodecFamily.Opus)]
    [InlineData(ExportContainer.Matroska, AudioCodecFamily.None)]
    public void Av1_is_preserved_for_supported_custom_containers(
        ExportContainer container,
        AudioCodecFamily audioCodec)
    {
        var settings = ExportPreferences.Default with
        {
            CustomContainer = container,
            CustomVideoCodec = VideoCodecFamily.Av1,
            CustomAudioCodec = audioCodec,
        };

        var normalized = settings.Normalize();

        Assert.Equal(VideoCodecFamily.Av1, normalized.CustomVideoCodec);
        Assert.Equal(audioCodec, normalized.CustomAudioCodec);
    }

    [Theory]
    [InlineData(ExportContainer.Mp4, VideoCodecFamily.Hevc, AudioCodecFamily.Aac)]
    [InlineData(ExportContainer.WebM, VideoCodecFamily.Vp8, AudioCodecFamily.Vorbis)]
    [InlineData(ExportContainer.Matroska, VideoCodecFamily.Hevc, AudioCodecFamily.Flac)]
    public void Extended_codec_combinations_are_preserved(
        ExportContainer container,
        VideoCodecFamily videoCodec,
        AudioCodecFamily audioCodec)
    {
        var settings = ExportPreferences.Default with
        {
            CustomContainer = container,
            CustomVideoCodec = videoCodec,
            CustomAudioCodec = audioCodec,
        };

        var normalized = settings.Normalize();

        Assert.Equal(videoCodec, normalized.CustomVideoCodec);
        Assert.Equal(audioCodec, normalized.CustomAudioCodec);
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
            VideoBitRateKbps = 9_500,
            EncodingSpeed = ExportEncodingSpeed.SmallerFile,
            HardwareAcceleration = ExportHardwareAcceleration.Automatic,
            VideoEncoder = ExportVideoEncoder.Software,
            HardwareDeviceIndex = 4,
        };

        viewModel.ApplyExportPreferences(preferences);

        Assert.Equal(BuiltInExportPresets.Custom, viewModel.SelectedExportPreset);
        Assert.Equal(52, viewModel.ExportScalePercent);
        Assert.Equal(61, viewModel.ExportQuality);
        Assert.Equal(ExportQualityMode.Custom, viewModel.ExportQualityMode);
        Assert.Equal(9_500, viewModel.ExportVideoBitRateKbps);
        Assert.True(viewModel.RememberExportAdjustments);
        Assert.Same(ExportContainerChoice.Matroska, viewModel.CustomExportContainer);
        Assert.Same(VideoCodecChoice.Vp9, viewModel.CustomVideoCodec);
        Assert.Same(AudioCodecChoice.None, viewModel.CustomAudioCodec);
        Assert.False(viewModel.CustomUseSourceFrameRate);
        Assert.Equal(50, viewModel.CustomFrameRate);
        Assert.Equal(ExportDestinationMode.Clipboard, viewModel.ExportDestination);
        Assert.Equal(175, viewModel.ExportPlaybackSpeedPercent);
        Assert.Equal(ExportEncodingSpeed.SmallerFile, viewModel.ExportEncodingSpeed);
        Assert.Equal(ExportHardwareAcceleration.Automatic, viewModel.ExportHardwareAcceleration);
        Assert.Equal(ExportVideoEncoder.Software, viewModel.PreferredExportVideoEncoder);
        Assert.Equal(4, viewModel.PreferredHardwareDeviceIndex);
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
            VideoBitRateKbps = 9_500,
            QualityMode = ExportQualityMode.Custom,
            RememberAdjustments = false,
        };

        viewModel.ApplyExportPreferences(preferences);

        Assert.False(viewModel.RememberExportAdjustments);
        Assert.Equal(100, viewModel.ExportScalePercent);
        Assert.Equal(75, viewModel.ExportQuality);
        Assert.Equal(ExportEncodingSettings.DefaultVideoBitRateKbps, viewModel.ExportVideoBitRateKbps);
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
            ExportVideoBitRateKbps = 9_500,
            SelectedExportQuality = ExportQualityChoice.Custom,
            SelectedExportGpu = ExportGpuChoice.FromValue(5),
        };

        var preferences = viewModel.CreateExportPreferences();

        Assert.False(preferences.RememberAdjustments);
        Assert.Equal(100, preferences.ScalePercent);
        Assert.Equal(75, preferences.Quality);
        Assert.Equal(100, preferences.PlaybackSpeedPercent);
        Assert.Equal(ExportQualityMode.MatchSource, preferences.QualityMode);
        Assert.Equal(ExportEncodingSettings.DefaultVideoBitRateKbps, preferences.VideoBitRateKbps);
        Assert.Equal(5, preferences.HardwareDeviceIndex);
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
