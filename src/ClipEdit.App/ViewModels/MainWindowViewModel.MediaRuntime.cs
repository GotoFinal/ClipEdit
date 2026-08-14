using ClipEdit.App.Settings;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _preferSystemMediaTools;
    private string? _configuredFfmpegPath;
    private string? _configuredFfprobePath;
    private string? _configuredLibMpvPath;
    private string? _previewLibMpvPath;

    public bool PreferSystemMediaTools
    {
        get => _preferSystemMediaTools;
        set => SetProperty(ref _preferSystemMediaTools, value);
    }

    public string? ConfiguredFfmpegPath
    {
        get => _configuredFfmpegPath;
        set => SetProperty(ref _configuredFfmpegPath, NormalizeOptionalPath(value));
    }

    public string? ConfiguredFfprobePath
    {
        get => _configuredFfprobePath;
        set => SetProperty(ref _configuredFfprobePath, NormalizeOptionalPath(value));
    }

    public string? ConfiguredLibMpvPath
    {
        get => _configuredLibMpvPath;
        set => SetProperty(ref _configuredLibMpvPath, NormalizeOptionalPath(value));
    }

    public string? PreviewLibMpvPath
    {
        get => _previewLibMpvPath;
        private set => SetProperty(ref _previewLibMpvPath, value);
    }

    internal void ConfigureMediaRuntime(
        MediaRuntimeSettings settings,
        string? previewLibMpvPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PreferSystemMediaTools = settings.PreferSystemMediaTools;
        ConfiguredFfmpegPath = settings.FfmpegPath;
        ConfiguredFfprobePath = settings.FfprobePath;
        ConfiguredLibMpvPath = settings.LibMpvPath;
        PreviewLibMpvPath = previewLibMpvPath;
    }

    internal MediaRuntimeSettings CreateMediaRuntimeSettings() => new(
        PreferSystemMediaTools,
        ConfiguredFfmpegPath,
        ConfiguredFfprobePath,
        ConfiguredLibMpvPath);

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
