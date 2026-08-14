using System.Text.Json;

namespace ClipEdit.App.Settings;

internal sealed record MediaRuntimeSettings(
    bool PreferSystemMediaTools,
    string? FfmpegPath,
    string? FfprobePath,
    string? LibMpvPath)
{
    private const int MaximumPathLength = 4_096;

    public static MediaRuntimeSettings Default { get; } = new(true, null, null, null);

    public MediaRuntimeSettings Normalize() => new(
        PreferSystemMediaTools,
        NormalizePath(FfmpegPath),
        NormalizePath(FfprobePath),
        NormalizePath(LibMpvPath));

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        return trimmed.Length <= MaximumPathLength ? trimmed : null;
    }
}

internal sealed class MediaRuntimeSettingsStore
{
    private const long MaximumSettingsBytes = 32 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public MediaRuntimeSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public MediaRuntimeSettings Load()
    {
        try
        {
            var info = new FileInfo(_settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return MediaRuntimeSettings.Default;
            }

            return (JsonSerializer.Deserialize<MediaRuntimeSettings>(
                        File.ReadAllText(_settingsPath),
                        SerializerOptions) ?? MediaRuntimeSettings.Default)
                .Normalize();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return MediaRuntimeSettings.Default;
        }
    }

    public bool Save(MediaRuntimeSettings settings)
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
