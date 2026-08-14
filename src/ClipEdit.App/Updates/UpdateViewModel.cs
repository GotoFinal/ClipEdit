using System.Reflection;
using System.Runtime.InteropServices;
using ClipEdit.App.ViewModels;

namespace ClipEdit.App.Updates;

public sealed class UpdateViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    private readonly IUpdateClient? _client;
    private readonly UpdateSettingsStore? _settingsStore;
    private readonly SemanticVersion _currentVersion;
    private readonly string? _releaseAssetId;
    private readonly string? _stagingRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _canSelfUpdate;
    private UpdateSettings _settings;
    private AvailableUpdate? _availableUpdate;
    private bool _isChecking;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _statusText;

    public UpdateViewModel()
    {
        _currentVersion = GetCurrentVersion();
        _settings = UpdateSettings.Default with { AutomaticallyCheckForUpdates = false };
        _utcNow = static () => DateTimeOffset.UtcNow;
        _statusText = "Automatic updates are available in packaged Windows and Linux builds.";
    }

    internal UpdateViewModel(
        IUpdateClient client,
        UpdateSettingsStore settingsStore,
        string releaseAssetId,
        string stagingRoot,
        SemanticVersion currentVersion,
        bool canSelfUpdate,
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _releaseAssetId = releaseAssetId;
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _canSelfUpdate = canSelfUpdate;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _settings = settingsStore.Load();
        _statusText = canSelfUpdate
            ? "ClipEdit checks GitHub Releases without sending project or media information."
            : "This development build can check releases but cannot replace itself.";
        ApplyCachedUpdate(_settings.CachedUpdate);
    }

    public string CurrentVersionText => $"Version {_currentVersion}";

    public bool AutomaticallyCheckForUpdates
    {
        get => _settings.AutomaticallyCheckForUpdates;
        set
        {
            if (value == _settings.AutomaticallyCheckForUpdates)
            {
                return;
            }

            _settings = _settings with { AutomaticallyCheckForUpdates = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool IncludeBetaVersions
    {
        get => _settings.IncludeBetaVersions;
        set
        {
            if (value == _settings.IncludeBetaVersions)
            {
                return;
            }

            _settings = _settings with
            {
                IncludeBetaVersions = value,
                LastCheckUtc = null,
            };
            OnPropertyChanged();
            if (!value && _availableUpdate?.IsPrerelease == true)
            {
                _availableUpdate = null;
                _settings = _settings with { CachedUpdate = null };
                RaiseUpdateChanged();
            }
            StatusText = value
                ? "Beta releases enabled; use Check now to look for prereleases."
                : "Only stable GitHub releases will be offered.";
            SaveSettings();
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                RaiseActionStateChanged();
            }
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                RaiseActionStateChanged();
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(UpdateActionText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(UpdateToolTip));
            }
        }
    }

    public bool HasAvailableUpdate => _availableUpdate is not null;

    public bool ShowUpdateButton => HasAvailableUpdate && _canSelfUpdate;

    public bool CanCheckForUpdates => _client is not null && !IsChecking && !IsDownloading;

    public bool CanApplyUpdate => ShowUpdateButton && !IsChecking && !IsDownloading;

    public string CheckButtonText => IsChecking ? "Checking…" : "Check now";

    public string UpdateActionText => IsDownloading
        ? $"Update {DownloadProgress:P0}"
        : "Update";

    public string UpdateToolTip => _availableUpdate is null
        ? StatusText
        : $"Install ClipEdit {_availableUpdate.Version} and restart · {_availableUpdate.Asset.Size / (1024d * 1024d):0.#} MB";

    internal static SemanticVersion GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateViewModel).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var semanticVersion))
        {
            return semanticVersion!;
        }

        var assemblyVersion = assembly.GetName().Version;
        var fallback = assemblyVersion is null
            ? "0.0.0"
            : $"{Math.Max(0, assemblyVersion.Major)}.{Math.Max(0, assemblyVersion.Minor)}.{Math.Max(0, assemblyVersion.Build)}";
        return SemanticVersion.TryParse(fallback, out semanticVersion)
            ? semanticVersion!
            : throw new InvalidOperationException("ClipEdit's application version is invalid.");
    }

    internal static string? GetCurrentRuntimeId()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }
        return OperatingSystem.IsLinux() ? "linux-x64" : null;
    }

    internal static string? GetCurrentReleaseAssetId()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateViewModel).Assembly;
        var configuredAssetId = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                "ClipEditReleaseAssetId",
                StringComparison.Ordinal))?
            .Value;
        return ResolveReleaseAssetId(configuredAssetId, GetCurrentRuntimeId());
    }

    internal static string? ResolveReleaseAssetId(string? configuredAssetId, string? runtimeId) =>
        configuredAssetId switch
        {
            "win-x64" when runtimeId == "win-x64" => configuredAssetId,
            "linux-x64" when runtimeId == "linux-x64" => configuredAssetId,
            "linux-x64-system" when runtimeId == "linux-x64" => configuredAssetId,
            _ => runtimeId,
        };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!AutomaticallyCheckForUpdates || _client is null || _releaseAssetId is null)
        {
            return;
        }

        var elapsed = _settings.LastCheckUtc is { } lastCheck
            ? _utcNow() - lastCheck
            : TimeSpan.MaxValue;
        if (elapsed < AutomaticCheckInterval)
        {
            return;
        }

        await CheckAsync(isManual: false, cancellationToken);
    }

    public Task CheckNowAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(isManual: true, cancellationToken);

    internal async Task<StagedUpdate?> PrepareUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_client is null || _availableUpdate is null || _stagingRoot is null || !_canSelfUpdate)
        {
            return null;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = $"Downloading ClipEdit {_availableUpdate.Version}…";
        try
        {
            var progress = new Progress<double>(value => DownloadProgress = value);
            var staged = await _client.DownloadAsync(
                _availableUpdate,
                _stagingRoot,
                progress,
                cancellationToken);
            StatusText = "Update verified; restarting ClipEdit…";
            return staged;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Update download canceled.";
            return null;
        }
        catch (UpdateException exception)
        {
            StatusText = exception.Message;
            return null;
        }
        catch (Exception exception)
        {
            StatusText = $"Could not stage the update: {exception.Message}";
            return null;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public void ReportInstallFailure(string message)
    {
        StatusText = message;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    private async Task CheckAsync(bool isManual, CancellationToken cancellationToken)
    {
        if (_client is null || _releaseAssetId is null || IsChecking || IsDownloading)
        {
            return;
        }

        IsChecking = true;
        if (isManual)
        {
            StatusText = "Checking GitHub Releases…";
        }

        try
        {
            var update = await _client.CheckAsync(
                _currentVersion,
                _releaseAssetId,
                IncludeBetaVersions,
                cancellationToken);
            _availableUpdate = update;
            _settings = _settings with
            {
                LastCheckUtc = _utcNow(),
                CachedUpdate = update is null ? null : ToCachedSettings(update, _releaseAssetId),
            };
            SaveSettings();
            StatusText = update is null
                ? $"ClipEdit {_currentVersion} is up to date for {_releaseAssetId}."
                : $"ClipEdit {update.Version} is available for {_releaseAssetId}.";
            RaiseUpdateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isManual)
            {
                StatusText = "Update check canceled.";
            }
        }
        catch (UpdateException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception exception)
        {
            StatusText = $"Could not check for updates: {exception.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyCachedUpdate(CachedUpdateSettings? cached)
    {
        if (cached is null ||
            _releaseAssetId is null ||
            !string.Equals(cached.RuntimeId, _releaseAssetId, StringComparison.Ordinal) ||
            (cached.IsPrerelease && !IncludeBetaVersions) ||
            !SemanticVersion.TryParse(cached.Version, out var version) ||
            version <= _currentVersion ||
            !Uri.TryCreate(cached.ReleasePageUrl, UriKind.Absolute, out var releasePage) ||
            !Uri.TryCreate(cached.AssetDownloadUrl, UriKind.Absolute, out var assetDownload))
        {
            return;
        }

        Uri? checksum = null;
        if (cached.ChecksumDownloadUrl is not null &&
            !Uri.TryCreate(cached.ChecksumDownloadUrl, UriKind.Absolute, out checksum))
        {
            return;
        }

        _availableUpdate = new AvailableUpdate(
            version!,
            cached.TagName,
            cached.DisplayName,
            releasePage,
            cached.PublishedAt,
            cached.IsPrerelease,
            new UpdateAsset(
                cached.AssetName,
                assetDownload,
                cached.AssetSize,
                cached.AssetSha256,
                checksum));
    }

    private static CachedUpdateSettings ToCachedSettings(AvailableUpdate update, string releaseAssetId) =>
        new(
            update.Version.ToString(),
            update.TagName,
            update.DisplayName,
            update.ReleasePageUri.AbsoluteUri,
            update.PublishedAt,
            update.IsPrerelease,
            releaseAssetId,
            update.Asset.Name,
            update.Asset.DownloadUri.AbsoluteUri,
            update.Asset.Size,
            update.Asset.Sha256,
            update.Asset.ChecksumDownloadUri?.AbsoluteUri);

    private void SaveSettings()
    {
        _settingsStore?.Save(_settings);
    }

    private void RaiseUpdateChanged()
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(ShowUpdateButton));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(UpdateToolTip));
        OnPropertyChanged(nameof(CanApplyUpdate));
    }

    private void RaiseActionStateChanged()
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanApplyUpdate));
        OnPropertyChanged(nameof(CheckButtonText));
        OnPropertyChanged(nameof(UpdateActionText));
    }
}
