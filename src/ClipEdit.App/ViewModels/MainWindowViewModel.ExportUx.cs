using ClipEdit.Domain.Geometry;
using ClipEdit.Media.Export;

namespace ClipEdit.App.ViewModels;

public enum ExportDestinationMode
{
    File,
    Clipboard,
    FileAndClipboard,
}

public sealed record ExportDestinationChoice(
    ExportDestinationMode Value,
    string DisplayName)
{
    public static ExportDestinationChoice File { get; } = new(
        ExportDestinationMode.File,
        "File");

    public static ExportDestinationChoice Clipboard { get; } = new(
        ExportDestinationMode.Clipboard,
        "Clipboard");

    public static ExportDestinationChoice FileAndClipboard { get; } = new(
        ExportDestinationMode.FileAndClipboard,
        "File + clipboard");

    public static IReadOnlyList<ExportDestinationChoice> All { get; } =
        [File, Clipboard, FileAndClipboard];

    public static ExportDestinationChoice FromValue(ExportDestinationMode value) => value switch
    {
        ExportDestinationMode.Clipboard => Clipboard,
        ExportDestinationMode.FileAndClipboard => FileAndClipboard,
        _ => File,
    };
}

public sealed record ExportQualityChoice(
    ExportQualityMode Value,
    string DisplayName)
{
    public static ExportQualityChoice MatchSource { get; } = new(
        ExportQualityMode.MatchSource,
        "Match input");

    public static ExportQualityChoice Custom { get; } = new(
        ExportQualityMode.Custom,
        "Custom");

    public static IReadOnlyList<ExportQualityChoice> All { get; } =
        [MatchSource, Custom];

    public static ExportQualityChoice FromValue(ExportQualityMode value) => value switch
    {
        ExportQualityMode.Custom => Custom,
        _ => MatchSource,
    };
}

public sealed partial class MainWindowViewModel
{
    private static readonly double[] ExportPlaybackSpeedSnapValues =
    [
        .. new[]
        {
            1, 10, 25, 50, 75, 100, 125, 150, 175, 200, 250, 300, 400, 500,
            750, 1_000, 1_500, 2_000, 3_000, 4_000, 5_000, 7_500, 10_000,
        }.Select(PlaybackSpeedPercentToSliderValue),
    ];

    public IReadOnlyList<ExportDestinationChoice> ExportDestinationChoices =>
        ExportDestinationChoice.All;

    public IReadOnlyList<ExportQualityChoice> ExportQualityChoices =>
        ExportQualityChoice.All;

    public IReadOnlyList<double> ExportPlaybackSpeedSliderSnapValues =>
        ExportPlaybackSpeedSnapValues;

    public ExportDestinationChoice SelectedExportDestination
    {
        get => _selectedExportDestination;
        set
        {
            var next = value ?? ExportDestinationChoice.File;
            if (SetProperty(ref _selectedExportDestination, next))
            {
                OnPropertyChanged(nameof(ExportActionText));
                OnPropertyChanged(nameof(ExportDestination));
                OnPropertyChanged(nameof(ExportSettingsSummary));
            }
        }
    }

    public ExportDestinationMode ExportDestination => SelectedExportDestination.Value;

    public ExportQualityChoice SelectedExportQuality
    {
        get => _selectedExportQuality;
        set
        {
            var next = value ?? ExportQualityChoice.MatchSource;
            if (SetProperty(ref _selectedExportQuality, next))
            {
                OnPropertyChanged(nameof(ExportQualityMode));
                OnPropertyChanged(nameof(UsesCustomExportQuality));
                OnPropertyChanged(nameof(UsesMatchedInputQuality));
                RaiseExportStateChanged();
            }
        }
    }

    public ExportQualityMode ExportQualityMode => SelectedExportQuality.Value;

    public bool UsesCustomExportQuality =>
        IsGifExport || ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.Custom;

    public bool UsesMatchedInputQuality =>
        !IsGifExport && ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.MatchSource;

    public bool RememberExportAdjustments
    {
        get => _rememberExportAdjustments;
        set => SetProperty(ref _rememberExportAdjustments, value);
    }

    public string ExportActionText => ExportDestination == ExportDestinationMode.Clipboard
        ? "Copy"
        : "Export";

    public int ExportScalePercent
    {
        get => _exportScalePercent;
        set
        {
            var next = Math.Clamp(value, 10, 100);
            if (SetProperty(ref _exportScalePercent, next))
            {
                OnPropertyChanged(nameof(ExportScaleSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportScaleSliderValue
    {
        get => ExportScalePercent;
        set => ExportScalePercent = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int ExportQuality
    {
        get => _exportQuality;
        set
        {
            var next = Math.Clamp(value, 1, 100);
            if (SetProperty(ref _exportQuality, next))
            {
                OnPropertyChanged(nameof(ExportQualitySliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportQualitySliderValue
    {
        get => ExportQuality;
        set => ExportQuality = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int GifFrameRate
    {
        get => _gifFrameRate;
        set
        {
            var next = Math.Clamp(value, 1, 60);
            if (SetProperty(ref _gifFrameRate, next))
            {
                OnPropertyChanged(nameof(GifFrameRateSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double GifFrameRateSliderValue
    {
        get => GifFrameRate;
        set => GifFrameRate = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public int ExportPlaybackSpeedPercent
    {
        get => _exportPlaybackSpeedPercent;
        set
        {
            var next = Math.Clamp(
                value,
                ExportEncodingSettings.MinimumPlaybackSpeedPercent,
                ExportEncodingSettings.MaximumPlaybackSpeedPercent);
            if (SetProperty(ref _exportPlaybackSpeedPercent, next))
            {
                OnPropertyChanged(nameof(ExportPlaybackSpeedSliderValue));
                RaiseExportStateChanged();
            }
        }
    }

    public double ExportPlaybackSpeedSliderValue
    {
        get => PlaybackSpeedPercentToSliderValue(ExportPlaybackSpeedPercent);
        set => ExportPlaybackSpeedPercent = SliderValueToPlaybackSpeedPercent(value);
    }

    internal static double PlaybackSpeedPercentToSliderValue(int playbackSpeedPercent) =>
        Math.Log10(Math.Clamp(
            playbackSpeedPercent,
            ExportEncodingSettings.MinimumPlaybackSpeedPercent,
            ExportEncodingSettings.MaximumPlaybackSpeedPercent)) * 25;

    internal static int SliderValueToPlaybackSpeedPercent(double sliderValue) =>
        (int)Math.Round(
            Math.Pow(10, Math.Clamp(sliderValue, 0, 100) / 25),
            MidpointRounding.AwayFromZero);

    public bool IsGifExport => GetEffectiveExportPreset().VideoCodec == VideoCodecFamily.Gif;

    public string ExportOutputSizeText
    {
        get
        {
            var size = CurrentExportEncodingSettings.CalculateOutputSize(
                CanvasCrop.ExportSize,
                GetEffectiveExportPreset().RequiresEvenDimensions);
            return $"{size.Width} × {size.Height}";
        }
    }

    public string ExportSettingsSummary
    {
        get
        {
            var destination = ExportDestination switch
            {
                ExportDestinationMode.Clipboard => "Clipboard · ",
                ExportDestinationMode.FileAndClipboard => "File + clipboard · ",
                _ => string.Empty,
            };
            var speed = ExportPlaybackSpeedPercent == 100
                ? string.Empty
                : $" · {ExportPlaybackSpeedPercent}% speed";
            var quality = IsGifExport || ExportQualityMode == ClipEdit.Media.Export.ExportQualityMode.Custom
                ? $"quality {ExportQuality}%"
                : "match input quality";
            return IsGifExport
                ? $"{destination}{ExportOutputSizeText} · {quality} · {GifFrameRate} fps{speed}"
                : $"{destination}{ExportOutputSizeText} · {quality}{speed}";
        }
    }

    private ExportEncodingSettings CurrentExportEncodingSettings => new(
        ExportQuality,
        ExportScalePercent,
        GifFrameRate,
        ExportPlaybackSpeedPercent,
        IsGifExport ? ClipEdit.Media.Export.ExportQualityMode.Custom : ExportQualityMode);

    private void ResetTransientExportAdjustments()
    {
        SelectedExportQuality = ExportQualityChoice.FromValue(ExportEncodingSettings.DefaultQualityMode);
        ExportQuality = ExportEncodingSettings.DefaultQuality;
        ExportScalePercent = ExportEncodingSettings.DefaultScalePercent;
        GifFrameRate = ExportEncodingSettings.DefaultGifFrameRate;
        ExportPlaybackSpeedPercent = ExportEncodingSettings.DefaultPlaybackSpeedPercent;
    }

    public bool HasExportBlockingIssue => !IsExporting && ExportAvailabilityText != "Ready to export";

    public bool CanFixExportCompatibility => TryGetCompatibleOutputSize(out _);

    public string ExportCompatibilityActionText =>
        TryGetCompatibleOutputSize(out var size)
            ? $"Use {size.Width} × {size.Height}"
            : "Fix export";

    public bool MakeExportCropCompatible()
    {
        var slices = GetSequenceExportSlices();
        if (slices.Count == 0 || !TryGetCompatibleOutputSize(out var compatibleSize))
        {
            StatusText = ExportAvailabilityText;
            return false;
        }

        var crop = CanvasCrop;
        var x = Math.Clamp(crop.X, 0, crop.SourceSize.Width - compatibleSize.Width);
        var y = Math.Clamp(crop.Y, 0, crop.SourceSize.Height - compatibleSize.Height);
        CanvasCrop = new CropRegion(
            crop.SourceSize,
            x,
            y,
            compatibleSize.Width,
            compatibleSize.Height);
        StatusText = $"Crop changed to {compatibleSize.Width} × {compatibleSize.Height}; export is ready";
        RaiseExportStateChanged();
        return true;
    }

    private bool TryGetCompatibleOutputSize(out PixelSize compatibleSize)
    {
        compatibleSize = default;
        if (!GetEffectiveExportPreset().RequiresEvenDimensions)
        {
            return false;
        }

        var slices = GetSequenceExportSlices();
        if (slices.Count == 0)
        {
            return false;
        }

        var crop = CanvasCrop;
        var output = CurrentExportEncodingSettings.CalculateOutputSize(crop.ExportSize, false);
        if ((output.Width & 1) == 0 && (output.Height & 1) == 0)
        {
            return false;
        }

        compatibleSize = new PixelSize(
            ToEvenDimension(crop.Width, crop.SourceSize.Width),
            ToEvenDimension(crop.Height, crop.SourceSize.Height));
        return true;
    }

    private static int ToEvenDimension(int value, int maximum)
    {
        if ((value & 1) == 0)
        {
            return value;
        }

        return value > 1 ? value - 1 : Math.Min(2, maximum);
    }
}
