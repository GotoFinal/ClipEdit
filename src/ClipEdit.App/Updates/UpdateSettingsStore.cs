using System.Text.Json;

namespace ClipEdit.App.Updates;

internal sealed record CachedUpdateSettings(
    string Version,
    string TagName,
    string DisplayName,
    string ReleasePageUrl,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    string RuntimeId,
    string AssetName,
    string AssetDownloadUrl,
    long AssetSize,
    string? AssetSha256,
    string? ChecksumDownloadUrl);

internal sealed record UpdateSettings(
    bool AutomaticallyCheckForUpdates,
    bool IncludeBetaVersions,
    DateTimeOffset? LastCheckUtc,
    CachedUpdateSettings? CachedUpdate)
{
    public static UpdateSettings Default { get; } = new(true, false, null, null);
}

internal sealed class UpdateSettingsStore
{
    private const long MaximumSettingsBytes = 32 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public UpdateSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public UpdateSettings Load()
    {
        try
        {
            var info = new FileInfo(_settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return UpdateSettings.Default;
            }

            return JsonSerializer.Deserialize<UpdateSettings>(
                       File.ReadAllText(_settingsPath),
                       SerializerOptions) ?? UpdateSettings.Default;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdateSettings.Default;
        }
    }

    public bool Save(UpdateSettings settings)
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
                JsonSerializer.Serialize(settings, SerializerOptions));
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
