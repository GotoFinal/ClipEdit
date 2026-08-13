using System.Text.Json;

namespace ClipEdit.App.Settings;

internal sealed record CanvasInteractionSettings(
    double WheelZoomPercent,
    int WheelRotationDegrees,
    int ClipboardExportMaximumMegabytes)
{
    public const int MinimumClipboardExportMegabytes = 1;
    public const int MaximumClipboardExportMegabytes = 4_096;
    public const int DefaultClipboardExportMegabytes = 100;

    public static CanvasInteractionSettings Default { get; } = new(
        10,
        1,
        DefaultClipboardExportMegabytes);

    public CanvasInteractionSettings Normalize()
    {
        var zoom = double.IsFinite(WheelZoomPercent)
            ? Math.Clamp(WheelZoomPercent, 1, 50)
            : Default.WheelZoomPercent;
        var rotation = Math.Clamp(WheelRotationDegrees, 1, 45);
        var clipboardMaximum = ClipboardExportMaximumMegabytes <= 0
            ? DefaultClipboardExportMegabytes
            : Math.Clamp(
                ClipboardExportMaximumMegabytes,
                MinimumClipboardExportMegabytes,
                MaximumClipboardExportMegabytes);
        return new CanvasInteractionSettings(zoom, rotation, clipboardMaximum);
    }
}

internal sealed class CanvasInteractionSettingsStore
{
    private const long MaximumSettingsBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public CanvasInteractionSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public CanvasInteractionSettings Load()
    {
        try
        {
            var info = new FileInfo(_settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return CanvasInteractionSettings.Default;
            }

            return (JsonSerializer.Deserialize<CanvasInteractionSettings>(
                        File.ReadAllText(_settingsPath),
                        SerializerOptions) ?? CanvasInteractionSettings.Default)
                .Normalize();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CanvasInteractionSettings.Default;
        }
    }

    public bool Save(CanvasInteractionSettings settings)
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
                JsonSerializer.Serialize(settings.Normalize(), SerializerOptions));
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
