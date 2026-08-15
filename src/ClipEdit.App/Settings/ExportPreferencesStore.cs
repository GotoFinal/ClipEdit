using System.Text.Json;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Export;
using ClipEdit.Media.Export;

namespace ClipEdit.App.Settings;

internal sealed record ExportPreferences(
    string SelectedExportPresetId,
    int ScalePercent,
    int Quality,
    int GifFrameRate,
    ExportContainer CustomContainer,
    VideoCodecFamily CustomVideoCodec,
    AudioCodecFamily CustomAudioCodec,
    bool CustomUseSourceFrameRate,
    int CustomFrameRate,
    ExportDestinationMode ExportDestination,
    IReadOnlyList<SavedExportPresetViewModel> SavedPresets,
    int PlaybackSpeedPercent = 100,
    bool RememberAdjustments = false,
    ExportQualityMode QualityMode = ExportQualityMode.MatchSource)
{
    public static ExportPreferences Default { get; } = new(
        BuiltInExportPresets.Mp4Compatible.Id,
        100,
        75,
        15,
        ExportContainer.Mp4,
        VideoCodecFamily.H264,
        AudioCodecFamily.Aac,
        true,
        30,
        ExportDestinationMode.File,
        [],
        100,
        false,
        ExportQualityMode.MatchSource);

    public ExportPreferences Normalize()
    {
        var presetId = BuiltInExportPresets.All.Any(preset => preset.Id == SelectedExportPresetId)
            ? SelectedExportPresetId
            : Default.SelectedExportPresetId;
        var custom = NormalizeCodecCombination(
            CustomContainer,
            CustomVideoCodec,
            CustomAudioCodec);
        var saved = (SavedPresets ?? [])
            .Where(preset =>
                !string.IsNullOrWhiteSpace(preset.Name) &&
                preset.Name.Length <= 60 &&
                preset.FrameRate is >= 1 and <= 120 &&
                preset.ScalePercent is >= 10 and <= 100 &&
                preset.Quality is >= 1 and <= 100 &&
                preset.GifFrameRate is >= 1 and <= 60 &&
                preset.PlaybackSpeedPercent is >= ExportEncodingSettings.MinimumPlaybackSpeedPercent and
                    <= ExportEncodingSettings.MaximumPlaybackSpeedPercent &&
                Enum.IsDefined(preset.QualityMode) &&
                Enum.IsDefined(preset.Container) &&
                Enum.IsDefined(preset.VideoCodec) &&
                Enum.IsDefined(preset.AudioCodec))
            .Select(preset =>
            {
                var codecs = NormalizeCodecCombination(
                    preset.Container,
                    preset.VideoCodec,
                    preset.AudioCodec);
                return preset with
                {
                    Container = codecs.Container,
                    VideoCodec = codecs.Video,
                    AudioCodec = codecs.Audio,
                    Name = preset.Name.Trim(),
                };
            })
            .GroupBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(50)
            .ToArray();
        return this with
        {
            SelectedExportPresetId = presetId,
            ScalePercent = Math.Clamp(ScalePercent, 10, 100),
            Quality = Math.Clamp(Quality, 1, 100),
            GifFrameRate = Math.Clamp(GifFrameRate, 1, 60),
            PlaybackSpeedPercent = PlaybackSpeedPercent is >= ExportEncodingSettings.MinimumPlaybackSpeedPercent and
                <= ExportEncodingSettings.MaximumPlaybackSpeedPercent
                ? PlaybackSpeedPercent
                : Default.PlaybackSpeedPercent,
            QualityMode = Enum.IsDefined(QualityMode)
                ? QualityMode
                : Default.QualityMode,
            CustomContainer = custom.Container,
            CustomVideoCodec = custom.Video,
            CustomAudioCodec = custom.Audio,
            CustomFrameRate = Math.Clamp(CustomFrameRate, 1, 120),
            ExportDestination = Enum.IsDefined(ExportDestination)
                ? ExportDestination
                : Default.ExportDestination,
            SavedPresets = saved,
        };
    }

    private static (ExportContainer Container, VideoCodecFamily Video, AudioCodecFamily Audio)
        NormalizeCodecCombination(
            ExportContainer container,
            VideoCodecFamily video,
            AudioCodecFamily audio)
    {
        return container switch
        {
            ExportContainer.Mp4 => (container, VideoCodecFamily.H264,
                audio == AudioCodecFamily.None ? AudioCodecFamily.None : AudioCodecFamily.Aac),
            ExportContainer.WebM => (container, VideoCodecFamily.Vp9,
                audio == AudioCodecFamily.None ? AudioCodecFamily.None : AudioCodecFamily.Opus),
            ExportContainer.Matroska => (container,
                video is VideoCodecFamily.H264 or VideoCodecFamily.Vp9 ? video : VideoCodecFamily.H264,
                audio is AudioCodecFamily.Aac or AudioCodecFamily.Opus or AudioCodecFamily.None
                    ? audio
                    : AudioCodecFamily.Aac),
            ExportContainer.Gif => (container, VideoCodecFamily.Gif, AudioCodecFamily.None),
            _ => (ExportContainer.Mp4, VideoCodecFamily.H264, AudioCodecFamily.Aac),
        };
    }
}

internal sealed class ExportPreferencesStore
{
    private const long MaximumSettingsBytes = 128 * 1024;

    private readonly string _settingsPath;

    public ExportPreferencesStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public ExportPreferences Load()
    {
        try
        {
            var info = new FileInfo(_settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return ExportPreferences.Default;
            }

            return (JsonSerializer.Deserialize(
                        File.ReadAllText(_settingsPath),
                        AppSettingsJsonContext.Default.ExportPreferences) ??
                    ExportPreferences.Default)
                .Normalize();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ExportPreferences.Default;
        }
    }

    public bool Save(ExportPreferences settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.saving");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    settings.Normalize(),
                    AppSettingsJsonContext.Default.ExportPreferences));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
