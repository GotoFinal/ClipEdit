using ClipEdit.Domain.Geometry;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
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
        if ((crop.Width & 1) == 0 && (crop.Height & 1) == 0)
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
