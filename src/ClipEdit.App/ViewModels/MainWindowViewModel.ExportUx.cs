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

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<ExportDestinationChoice> ExportDestinationChoices =>
        ExportDestinationChoice.All;

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
                MarkProjectDirty();
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
                MarkProjectDirty();
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
                MarkProjectDirty();
            }
        }
    }

    public double GifFrameRateSliderValue
    {
        get => GifFrameRate;
        set => GifFrameRate = (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

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
            return IsGifExport
                ? $"{destination}{ExportOutputSizeText} · quality {ExportQuality}% · {GifFrameRate} fps"
                : $"{destination}{ExportOutputSizeText} · quality {ExportQuality}%";
        }
    }

    private ExportEncodingSettings CurrentExportEncodingSettings => new(
        ExportQuality,
        ExportScalePercent,
        GifFrameRate);

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
