using System.Text.Json;

namespace ClipEdit.App.Settings;

internal sealed record CanvasInteractionSettings(
    double WheelZoomPercent,
    int WheelRotationDegrees,
    int ClipboardExportMaximumMegabytes,
    bool HasShownProjectFileAssociationPrompt = false,
    int RecoveryRetentionDays = 7,
    int MaximumRecoveryFiles = 20)
{
    public const int MinimumClipboardExportMegabytes = 1;
    public const int MaximumClipboardExportMegabytes = 4_096;
    public const int DefaultClipboardExportMegabytes = 100;
    public const int MinimumRecoveryRetentionDays = 1;
    public const int MaximumRecoveryRetentionDays = 365;
    public const int DefaultRecoveryRetentionDays = 7;
    public const int MinimumRecoveryFiles = 1;
    public const int MaximumRecoveryFilesLimit = 200;
    public const int DefaultMaximumRecoveryFiles = 20;

    public static CanvasInteractionSettings Default { get; } = new(
        10,
        1,
        DefaultClipboardExportMegabytes,
        false,
        DefaultRecoveryRetentionDays,
        DefaultMaximumRecoveryFiles);

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
        var recoveryRetentionDays = RecoveryRetentionDays <= 0
            ? DefaultRecoveryRetentionDays
            : Math.Clamp(
                RecoveryRetentionDays,
                MinimumRecoveryRetentionDays,
                MaximumRecoveryRetentionDays);
        var maximumRecoveryFiles = MaximumRecoveryFiles <= 0
            ? DefaultMaximumRecoveryFiles
            : Math.Clamp(
                MaximumRecoveryFiles,
                MinimumRecoveryFiles,
                MaximumRecoveryFilesLimit);
        return new CanvasInteractionSettings(
            zoom,
            rotation,
            clipboardMaximum,
            HasShownProjectFileAssociationPrompt,
            recoveryRetentionDays,
            maximumRecoveryFiles);
    }
}

internal sealed class CanvasInteractionSettingsStore
{
    private const long MaximumSettingsBytes = 16 * 1024;

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

            return (JsonSerializer.Deserialize(
                        File.ReadAllText(_settingsPath),
                        AppSettingsJsonContext.Default.CanvasInteractionSettings) ??
                    CanvasInteractionSettings.Default)
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
                JsonSerializer.Serialize(
                    settings.Normalize(),
                    AppSettingsJsonContext.Default.CanvasInteractionSettings));
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
