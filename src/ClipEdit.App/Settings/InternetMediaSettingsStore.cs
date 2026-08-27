using System.Text.Json;

namespace ClipEdit.App.Settings;

internal sealed record InternetMediaSettings(
    int? PreferredVideoHeight,
    int? PreferredAudioBitrateKbps,
    int ConcurrentFragments = 4)
{
    public const int DefaultConcurrentFragments = 4;

    public static InternetMediaSettings Default { get; } = new(null, null, DefaultConcurrentFragments);

    public InternetMediaSettings Normalize() => new(
        PreferredVideoHeight is >= 144 and <= 8_640 ? PreferredVideoHeight : null,
        PreferredAudioBitrateKbps is >= 16 and <= 1_536 ? PreferredAudioBitrateKbps : null,
        Math.Clamp(ConcurrentFragments, 1, 16));
}

internal sealed class InternetMediaSettingsStore
{
    private const long MaximumSettingsBytes = 16 * 1024;
    private readonly string _settingsPath;

    public InternetMediaSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public InternetMediaSettings Load()
    {
        try
        {
            var info = new FileInfo(_settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return InternetMediaSettings.Default;
            }

            return (JsonSerializer.Deserialize(
                        File.ReadAllText(_settingsPath),
                        AppSettingsJsonContext.Default.InternetMediaSettings) ??
                    InternetMediaSettings.Default)
                .Normalize();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return InternetMediaSettings.Default;
        }
    }

    public bool Save(InternetMediaSettings settings)
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
                    AppSettingsJsonContext.Default.InternetMediaSettings));
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
