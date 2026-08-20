using System.Collections.ObjectModel;
using ClipEdit.Application.Export;
using ClipEdit.App.Settings;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public sealed record ExportContainerChoice(
    ExportContainer Value,
    string DisplayName,
    string FileExtension)
{
    public static ExportContainerChoice Mp4 { get; } = new(ExportContainer.Mp4, "MP4", ".mp4");
    public static ExportContainerChoice WebM { get; } = new(ExportContainer.WebM, "WebM", ".webm");
    public static ExportContainerChoice Matroska { get; } = new(ExportContainer.Matroska, "MKV", ".mkv");
    public static ExportContainerChoice Gif { get; } = new(ExportContainer.Gif, "GIF", ".gif");

    public static IReadOnlyList<ExportContainerChoice> All { get; } = [Mp4, WebM, Matroska, Gif];

    public static ExportContainerChoice FromValue(ExportContainer value) =>
        All.FirstOrDefault(choice => choice.Value == value) ?? Mp4;
}

public sealed record VideoCodecChoice(VideoCodecFamily Value, string DisplayName)
{
    public static VideoCodecChoice H264 { get; } = new(VideoCodecFamily.H264, "H.264");
    public static VideoCodecChoice Hevc { get; } = new(VideoCodecFamily.Hevc, "H.265 / HEVC");
    public static VideoCodecChoice Vp8 { get; } = new(VideoCodecFamily.Vp8, "VP8");
    public static VideoCodecChoice Vp9 { get; } = new(VideoCodecFamily.Vp9, "VP9");
    public static VideoCodecChoice Av1 { get; } = new(VideoCodecFamily.Av1, "AV1");
    public static VideoCodecChoice Gif { get; } = new(VideoCodecFamily.Gif, "GIF");

    public static VideoCodecChoice FromValue(VideoCodecFamily value) => value switch
    {
        VideoCodecFamily.H264 => H264,
        VideoCodecFamily.Hevc => Hevc,
        VideoCodecFamily.Vp8 => Vp8,
        VideoCodecFamily.Vp9 => Vp9,
        VideoCodecFamily.Av1 => Av1,
        VideoCodecFamily.Gif => Gif,
        _ => H264,
    };
}

public sealed record AudioCodecChoice(AudioCodecFamily Value, string DisplayName)
{
    public static AudioCodecChoice Aac { get; } = new(AudioCodecFamily.Aac, "AAC");
    public static AudioCodecChoice Opus { get; } = new(AudioCodecFamily.Opus, "Opus");
    public static AudioCodecChoice Vorbis { get; } = new(AudioCodecFamily.Vorbis, "Vorbis");
    public static AudioCodecChoice Flac { get; } = new(AudioCodecFamily.Flac, "FLAC");
    public static AudioCodecChoice None { get; } = new(AudioCodecFamily.None, "No audio");

    public static AudioCodecChoice FromValue(AudioCodecFamily value) => value switch
    {
        AudioCodecFamily.Aac => Aac,
        AudioCodecFamily.Opus => Opus,
        AudioCodecFamily.Vorbis => Vorbis,
        AudioCodecFamily.Flac => Flac,
        AudioCodecFamily.None => None,
        _ => Aac,
    };
}

public sealed record SavedExportPresetViewModel(
    string Name,
    ExportContainer Container,
    VideoCodecFamily VideoCodec,
    AudioCodecFamily AudioCodec,
    bool UseSourceFrameRate,
    int FrameRate,
    int ScalePercent,
    int Quality,
    int GifFrameRate,
    int PlaybackSpeedPercent = 100,
    ExportQualityMode QualityMode = ExportQualityMode.Custom,
    ExportEncodingSpeed EncodingSpeed = ExportEncodingSpeed.Balanced,
    ExportHardwareAcceleration HardwareAcceleration = ExportHardwareAcceleration.Software,
    ExportVideoEncoder VideoEncoder = ExportEncodingSettings.DefaultVideoEncoder);

public sealed partial class MainWindowViewModel
{
    private ExportContainerChoice _customExportContainer = ExportContainerChoice.Mp4;
    private VideoCodecChoice _customVideoCodec = VideoCodecChoice.H264;
    private AudioCodecChoice _customAudioCodec = AudioCodecChoice.Aac;
    private bool _customUseSourceFrameRate = true;
    private int _customFrameRate = 30;
    private string _customPresetName = string.Empty;
    private SavedExportPresetViewModel? _selectedSavedExportPreset;
    private bool _isApplyingCustomExportSettings;

    public event EventHandler? SavedExportPresetsChanged;

    public IReadOnlyList<ExportContainerChoice> CustomExportContainerChoices => ExportContainerChoice.All;

    public ObservableCollection<VideoCodecChoice> CustomVideoCodecChoices { get; } =
        [VideoCodecChoice.H264, VideoCodecChoice.Hevc, VideoCodecChoice.Av1];

    public ObservableCollection<AudioCodecChoice> CustomAudioCodecChoices { get; } =
        [AudioCodecChoice.Aac, AudioCodecChoice.None];

    public ObservableCollection<SavedExportPresetViewModel> SavedExportPresets { get; } = [];

    public bool IsCustomExport => SelectedExportPreset.Id == BuiltInExportPresets.Custom.Id;

    public bool CustomCanSetFrameRate => CustomExportContainer.Value != ExportContainer.Gif;

    public ExportContainerChoice CustomExportContainer
    {
        get => _customExportContainer;
        set
        {
            var next = value ?? ExportContainerChoice.Mp4;
            if (SetProperty(ref _customExportContainer, next))
            {
                NormalizeCustomCodecChoices();
                CustomExportSettingsChanged();
            }
        }
    }

    public VideoCodecChoice CustomVideoCodec
    {
        get => _customVideoCodec;
        set
        {
            var next = value ?? CustomVideoCodecChoices.First();
            if (SetProperty(ref _customVideoCodec, next))
            {
                CustomExportSettingsChanged();
            }
        }
    }

    public AudioCodecChoice CustomAudioCodec
    {
        get => _customAudioCodec;
        set
        {
            var next = value ?? CustomAudioCodecChoices.First();
            if (SetProperty(ref _customAudioCodec, next))
            {
                CustomExportSettingsChanged();
            }
        }
    }

    public bool CustomUseSourceFrameRate
    {
        get => _customUseSourceFrameRate;
        set
        {
            if (SetProperty(ref _customUseSourceFrameRate, value))
            {
                OnPropertyChanged(nameof(CustomUsesFixedFrameRate));
                CustomExportSettingsChanged();
            }
        }
    }

    public bool CustomUsesFixedFrameRate => !CustomUseSourceFrameRate && CustomCanSetFrameRate;

    public int CustomFrameRate
    {
        get => _customFrameRate;
        set
        {
            var next = Math.Clamp(value, 1, 120);
            if (SetProperty(ref _customFrameRate, next))
            {
                OnPropertyChanged(nameof(CustomFrameRateSliderValue));
                CustomExportSettingsChanged();
            }
        }
    }

    public double CustomFrameRateSliderValue
    {
        get => CustomFrameRate;
        set => CustomFrameRate = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public string CustomPresetName
    {
        get => _customPresetName;
        set
        {
            if (SetProperty(ref _customPresetName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSaveCustomExportPreset));
            }
        }
    }

    public bool CanSaveCustomExportPreset =>
        !string.IsNullOrWhiteSpace(CustomPresetName) && CustomPresetName.Trim().Length <= 60;

    public SavedExportPresetViewModel? SelectedSavedExportPreset
    {
        get => _selectedSavedExportPreset;
        set
        {
            if (SetProperty(ref _selectedSavedExportPreset, value))
            {
                OnPropertyChanged(nameof(HasSelectedSavedExportPreset));
            }
        }
    }

    public bool HasSelectedSavedExportPreset => SelectedSavedExportPreset is not null;

    public bool SaveCustomExportPreset()
    {
        if (!CanSaveCustomExportPreset)
        {
            StatusText = "Enter a preset name up to 60 characters";
            return false;
        }

        var saved = CreateSavedExportPreset(CustomPresetName.Trim());
        var existingIndex = SavedExportPresets
            .Select((preset, index) => (preset, index))
            .FirstOrDefault(item => string.Equals(
                item.preset.Name,
                saved.Name,
                StringComparison.OrdinalIgnoreCase))
            .index;
        var existing = SavedExportPresets.FirstOrDefault(preset =>
            string.Equals(preset.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            SavedExportPresets.Add(saved);
        }
        else
        {
            SavedExportPresets[existingIndex] = saved;
        }

        SelectedSavedExportPreset = saved;
        CustomPresetName = saved.Name;
        StatusText = $"Saved export preset {saved.Name}";
        SavedExportPresetsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool LoadSelectedCustomExportPreset()
    {
        if (SelectedSavedExportPreset is not { } saved)
        {
            return false;
        }

        ApplySavedExportPreset(saved);
        StatusText = $"Loaded export preset {saved.Name}";
        return true;
    }

    public bool DeleteSelectedCustomExportPreset()
    {
        if (SelectedSavedExportPreset is not { } saved || !SavedExportPresets.Remove(saved))
        {
            return false;
        }

        SelectedSavedExportPreset = SavedExportPresets.FirstOrDefault();
        StatusText = $"Deleted export preset {saved.Name}";
        SavedExportPresetsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void ReplaceSavedExportPresets(IEnumerable<SavedExportPresetViewModel>? presets)
    {
        SavedExportPresets.Clear();
        if (presets is not null)
        {
            foreach (var preset in presets
                         .Where(IsValidSavedExportPreset)
                         .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.Last())
                         .Take(50))
            {
                SavedExportPresets.Add(preset);
            }
        }

        SelectedSavedExportPreset = SavedExportPresets.FirstOrDefault();
    }

    internal void ApplyExportPreferences(ExportPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        var wasLoading = _isLoadingProject;
        _isLoadingProject = true;
        try
        {
            RememberExportAdjustments = normalized.RememberAdjustments;
            ApplyCustomExportSettings(
                normalized.CustomContainer,
                normalized.CustomVideoCodec,
                normalized.CustomAudioCodec,
                normalized.CustomUseSourceFrameRate,
                normalized.CustomFrameRate);
            if (RememberExportAdjustments)
            {
                ExportScalePercent = normalized.ScalePercent;
                ExportQuality = normalized.Quality;
                GifFrameRate = normalized.GifFrameRate;
                ExportPlaybackSpeedPercent = normalized.PlaybackSpeedPercent;
                SelectedExportQuality = ExportQualityChoice.FromValue(normalized.QualityMode);
            }
            else
            {
                ResetTransientExportAdjustments();
            }
            SelectedExportDestination = ExportDestinationChoice.FromValue(normalized.ExportDestination);
            SelectedExportEncodingSpeed = ExportEncodingSpeedChoice.FromValue(normalized.EncodingSpeed);
            SelectedExportHardwareAcceleration = ExportHardwareAccelerationChoice.FromValue(
                normalized.HardwareAcceleration);
            SelectPreferredExportVideoEncoder(normalized.VideoEncoder);
            ReplaceSavedExportPresets(normalized.SavedPresets);
            SelectedExportPreset = ExportPresets.FirstOrDefault(preset =>
                                       preset.Id == normalized.SelectedExportPresetId) ??
                                   BuiltInExportPresets.Mp4Compatible;
        }
        finally
        {
            _isLoadingProject = wasLoading;
        }
    }

    internal ExportPreferences CreateExportPreferences() => new(
        SelectedExportPreset.Id,
        RememberExportAdjustments ? ExportScalePercent : ExportEncodingSettings.DefaultScalePercent,
        RememberExportAdjustments ? ExportQuality : ExportEncodingSettings.DefaultQuality,
        RememberExportAdjustments ? GifFrameRate : ExportEncodingSettings.DefaultGifFrameRate,
        CustomExportContainer.Value,
        CustomVideoCodec.Value,
        CustomAudioCodec.Value,
        CustomUseSourceFrameRate,
        CustomFrameRate,
        ExportDestination,
        SavedExportPresets.ToArray(),
        RememberExportAdjustments
            ? ExportPlaybackSpeedPercent
            : ExportEncodingSettings.DefaultPlaybackSpeedPercent,
        RememberExportAdjustments,
        RememberExportAdjustments
            ? ExportQualityMode
            : ExportEncodingSettings.DefaultQualityMode,
        ExportEncodingSpeed,
        ExportHardwareAcceleration,
        PreferredExportVideoEncoder);

    internal void ApplyCustomExportSettings(
        ExportContainer container,
        VideoCodecFamily videoCodec,
        AudioCodecFamily audioCodec,
        bool useSourceFrameRate,
        int frameRate)
    {
        _isApplyingCustomExportSettings = true;
        try
        {
            CustomExportContainer = ExportContainerChoice.FromValue(container);
            CustomVideoCodec = CustomVideoCodecChoices.FirstOrDefault(choice => choice.Value == videoCodec) ??
                               CustomVideoCodecChoices.First();
            CustomAudioCodec = CustomAudioCodecChoices.FirstOrDefault(choice => choice.Value == audioCodec) ??
                               CustomAudioCodecChoices.First();
            CustomUseSourceFrameRate = useSourceFrameRate;
            CustomFrameRate = frameRate;
        }
        finally
        {
            _isApplyingCustomExportSettings = false;
        }

        CustomExportSettingsChanged();
    }

    private ExportPreset CreateCustomExportPreset()
    {
        FrameRate? frameRate = CustomUsesFixedFrameRate
            ? new FrameRate(CustomFrameRate, 1)
            : null;
        return new ExportPreset(
            BuiltInExportPresets.Custom.Id,
            BuiltInExportPresets.Custom.DisplayName,
            CustomExportContainer.FileExtension,
            CustomExportContainer.Value,
            CustomVideoCodec.Value,
            CustomAudioCodec.Value,
            requiresEvenDimensions: CustomExportContainer.Value != ExportContainer.Gif,
            frameRate: frameRate);
    }

    private SavedExportPresetViewModel CreateSavedExportPreset(string name) => new(
        name,
        CustomExportContainer.Value,
        CustomVideoCodec.Value,
        CustomAudioCodec.Value,
        CustomUseSourceFrameRate,
        CustomFrameRate,
        ExportScalePercent,
        ExportQuality,
        GifFrameRate,
        ExportPlaybackSpeedPercent,
        ExportQualityMode,
        ExportEncodingSpeed,
        ExportHardwareAcceleration,
        PreferredExportVideoEncoder);

    private void ApplySavedExportPreset(SavedExportPresetViewModel saved)
    {
        ApplyCustomExportSettings(
            saved.Container,
            saved.VideoCodec,
            saved.AudioCodec,
            saved.UseSourceFrameRate,
            saved.FrameRate);
        ExportScalePercent = saved.ScalePercent;
        ExportQuality = saved.Quality;
        GifFrameRate = saved.GifFrameRate;
        ExportPlaybackSpeedPercent = saved.PlaybackSpeedPercent;
        SelectedExportQuality = ExportQualityChoice.FromValue(saved.QualityMode);
        SelectedExportEncodingSpeed = ExportEncodingSpeedChoice.FromValue(saved.EncodingSpeed);
        SelectedExportHardwareAcceleration = ExportHardwareAccelerationChoice.FromValue(
            saved.HardwareAcceleration);
        SelectPreferredExportVideoEncoder(saved.VideoEncoder);
        SelectedExportPreset = BuiltInExportPresets.Custom;
        CustomPresetName = saved.Name;
    }

    private void NormalizeCustomCodecChoices()
    {
        IReadOnlyList<VideoCodecChoice> videoChoices = CustomExportContainer.Value switch
        {
            ExportContainer.Mp4 => [VideoCodecChoice.H264, VideoCodecChoice.Hevc, VideoCodecChoice.Av1],
            ExportContainer.WebM => [VideoCodecChoice.Vp8, VideoCodecChoice.Vp9, VideoCodecChoice.Av1],
            ExportContainer.Matroska =>
            [
                VideoCodecChoice.H264,
                VideoCodecChoice.Hevc,
                VideoCodecChoice.Vp8,
                VideoCodecChoice.Vp9,
                VideoCodecChoice.Av1,
            ],
            ExportContainer.Gif => [VideoCodecChoice.Gif],
            _ => [VideoCodecChoice.H264],
        };
        IReadOnlyList<AudioCodecChoice> audioChoices = CustomExportContainer.Value switch
        {
            ExportContainer.Mp4 => [AudioCodecChoice.Aac, AudioCodecChoice.None],
            ExportContainer.WebM => [AudioCodecChoice.Opus, AudioCodecChoice.Vorbis, AudioCodecChoice.None],
            ExportContainer.Matroska =>
            [
                AudioCodecChoice.Aac,
                AudioCodecChoice.Opus,
                AudioCodecChoice.Vorbis,
                AudioCodecChoice.Flac,
                AudioCodecChoice.None,
            ],
            ExportContainer.Gif => [AudioCodecChoice.None],
            _ => [AudioCodecChoice.Aac, AudioCodecChoice.None],
        };

        ReplaceChoices(CustomVideoCodecChoices, videoChoices);
        ReplaceChoices(CustomAudioCodecChoices, audioChoices);
        var nextVideo = CustomVideoCodecChoices.FirstOrDefault(choice => choice.Value == _customVideoCodec.Value) ??
                        CustomVideoCodecChoices.First();
        var nextAudio = CustomAudioCodecChoices.FirstOrDefault(choice => choice.Value == _customAudioCodec.Value) ??
                        CustomAudioCodecChoices.First();
        if (!ReferenceEquals(_customVideoCodec, nextVideo))
        {
            _customVideoCodec = nextVideo;
            OnPropertyChanged(nameof(CustomVideoCodec));
        }
        if (!ReferenceEquals(_customAudioCodec, nextAudio))
        {
            _customAudioCodec = nextAudio;
            OnPropertyChanged(nameof(CustomAudioCodec));
        }

        OnPropertyChanged(nameof(CustomCanSetFrameRate));
        OnPropertyChanged(nameof(CustomUsesFixedFrameRate));
    }

    private void CustomExportSettingsChanged()
    {
        if (_isApplyingCustomExportSettings)
        {
            return;
        }

        OnPropertyChanged(nameof(IsGifExport));
        RaiseExportStateChanged();
        MarkProjectDirty();
    }

    private static void ReplaceChoices<T>(ObservableCollection<T> target, IEnumerable<T> choices)
    {
        target.Clear();
        foreach (var choice in choices)
        {
            target.Add(choice);
        }
    }

    private static bool IsValidSavedExportPreset(SavedExportPresetViewModel preset) =>
        !string.IsNullOrWhiteSpace(preset.Name) &&
        preset.Name.Length <= 60 &&
        Enum.IsDefined(preset.Container) &&
        Enum.IsDefined(preset.VideoCodec) &&
        Enum.IsDefined(preset.AudioCodec) &&
        preset.FrameRate is >= 1 and <= 120 &&
        preset.ScalePercent is >= 10 and <= 100 &&
        preset.Quality is >= 1 and <= 100 &&
        preset.GifFrameRate is >= 1 and <= 60 &&
        Enum.IsDefined(preset.QualityMode) &&
        Enum.IsDefined(preset.EncodingSpeed) &&
        Enum.IsDefined(preset.HardwareAcceleration) &&
        Enum.IsDefined(preset.VideoEncoder) &&
        preset.PlaybackSpeedPercent is >= ExportEncodingSettings.MinimumPlaybackSpeedPercent and
            <= ExportEncodingSettings.MaximumPlaybackSpeedPercent;
}
