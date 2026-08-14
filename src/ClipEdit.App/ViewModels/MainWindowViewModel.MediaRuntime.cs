using ClipEdit.App.Settings;
using ClipEdit.Application.Media;
using ClipEdit.Media.FFmpeg.Analysis;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Frames;
using ClipEdit.Media.FFmpeg.Probe;

namespace ClipEdit.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan MediaRuntimeValidationDelay = TimeSpan.FromMilliseconds(350);
    private bool _preferSystemMediaTools = true;
    private string? _configuredFfmpegPath;
    private string? _configuredFfprobePath;
    private string? _configuredLibMpvPath;
    private string? _previewLibMpvPath;
    private string? _activeFfmpegPath;
    private string? _activeFfprobePath;
    private MediaRuntimeValidator? _mediaRuntimeValidator;
    private CancellationTokenSource? _mediaRuntimeValidationCancellation;
    private bool _isConfiguringMediaRuntime;

    public bool PreferSystemMediaTools
    {
        get => _preferSystemMediaTools;
        set
        {
            if (SetProperty(ref _preferSystemMediaTools, value))
            {
                ScheduleMediaRuntimeValidation();
            }
        }
    }

    public string? ConfiguredFfmpegPath
    {
        get => _configuredFfmpegPath;
        set
        {
            if (SetProperty(ref _configuredFfmpegPath, NormalizeOptionalPath(value)))
            {
                ScheduleMediaRuntimeValidation();
            }
        }
    }

    public string? ConfiguredFfprobePath
    {
        get => _configuredFfprobePath;
        set
        {
            if (SetProperty(ref _configuredFfprobePath, NormalizeOptionalPath(value)))
            {
                ScheduleMediaRuntimeValidation();
            }
        }
    }

    public string? ConfiguredLibMpvPath
    {
        get => _configuredLibMpvPath;
        set
        {
            if (SetProperty(ref _configuredLibMpvPath, NormalizeOptionalPath(value)))
            {
                ScheduleMediaRuntimeValidation();
            }
        }
    }

    public string? PreviewLibMpvPath
    {
        get => _previewLibMpvPath;
        private set => SetProperty(ref _previewLibMpvPath, value);
    }

    public MediaRuntimeToolStatusViewModel FfmpegRuntimeStatus { get; } = new();

    public MediaRuntimeToolStatusViewModel FfprobeRuntimeStatus { get; } = new();

    public MediaRuntimeToolStatusViewModel LibMpvRuntimeStatus { get; } = new();

    internal void ConfigureMediaRuntime(
        MediaRuntimeSettings settings,
        string? activeFfmpegPath,
        string? activeFfprobePath,
        string? previewLibMpvPath,
        MediaRuntimeValidator mediaRuntimeValidator)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mediaRuntimeValidator);
        _isConfiguringMediaRuntime = true;
        try
        {
            PreferSystemMediaTools = settings.PreferSystemMediaTools;
            ConfiguredFfmpegPath = settings.FfmpegPath;
            ConfiguredFfprobePath = settings.FfprobePath;
            ConfiguredLibMpvPath = settings.LibMpvPath;
            _activeFfmpegPath = activeFfmpegPath;
            _activeFfprobePath = activeFfprobePath;
            PreviewLibMpvPath = previewLibMpvPath;
            _mediaRuntimeValidator = mediaRuntimeValidator;
        }
        finally
        {
            _isConfiguringMediaRuntime = false;
        }

        ScheduleMediaRuntimeValidation(immediate: true);
    }

    internal MediaRuntimeSettings CreateMediaRuntimeSettings() => new(
        PreferSystemMediaTools,
        ConfiguredFfmpegPath,
        ConfiguredFfprobePath,
        ConfiguredLibMpvPath);

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private void ScheduleMediaRuntimeValidation(bool immediate = false)
    {
        if (_isConfiguringMediaRuntime || _mediaRuntimeValidator is null)
        {
            return;
        }

        _mediaRuntimeValidationCancellation?.Cancel();
        _mediaRuntimeValidationCancellation?.Dispose();
        _mediaRuntimeValidationCancellation = new CancellationTokenSource();
        FfmpegRuntimeStatus.SetChecking();
        FfprobeRuntimeStatus.SetChecking();
        LibMpvRuntimeStatus.SetChecking();
        _ = ValidateMediaRuntimeAsync(
            CreateMediaRuntimeSettings(),
            immediate,
            _mediaRuntimeValidationCancellation.Token);
    }

    private async Task ValidateMediaRuntimeAsync(
        MediaRuntimeSettings settings,
        bool immediate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(MediaRuntimeValidationDelay, cancellationToken);
            }

            var validation = await _mediaRuntimeValidator!
                .ValidateAsync(settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyMediaRuntimeValidation(validation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal void ApplyMediaRuntimeValidation(MediaRuntimeValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        FfmpegRuntimeStatus.Apply(validation.Ffmpeg);
        FfprobeRuntimeStatus.Apply(validation.Ffprobe);
        var libMpvRequiresRestart = validation.LibMpv.IsValid &&
            !PathsEqual(validation.LibMpv.ResolvedPath, PreviewLibMpvPath);
        LibMpvRuntimeStatus.Apply(validation.LibMpv, libMpvRequiresRestart);

        if (validation.Ffmpeg is { IsValid: true, ResolvedPath: { } ffmpegPath } &&
            !PathsEqual(ffmpegPath, _activeFfmpegPath))
        {
            _frameDecoder = new FfmpegFrameDecoder(ffmpegPath);
            _waveformRenderer = new FfmpegWaveformRenderer(ffmpegPath);
            _exportRenderer = new FfmpegExportRenderer(ffmpegPath);
            _activeFfmpegPath = ffmpegPath;
            OnPropertyChanged(nameof(IsExportAvailable));
            OnPropertyChanged(nameof(ExportAvailabilityText));
            OnPropertyChanged(nameof(CanExport));
        }

        if (validation.Ffprobe is { IsValid: true, ResolvedPath: { } ffprobePath } &&
            !PathsEqual(ffprobePath, _activeFfprobePath))
        {
            _importMedia = new ImportMediaUseCase(new FfprobeMediaProbe(ffprobePath));
            _activeFfprobePath = ffprobePath;
            OnPropertyChanged(nameof(IsImportAvailable));
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right))
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private void DisposeMediaRuntimeValidation()
    {
        _mediaRuntimeValidationCancellation?.Cancel();
        _mediaRuntimeValidationCancellation?.Dispose();
        _mediaRuntimeValidationCancellation = null;
    }
}

public sealed class MediaRuntimeToolStatusViewModel : ViewModelBase
{
    private string _text = "Not checked";
    private string? _details;
    private bool _isChecking;
    private bool _isValid;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string? Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    public bool IsValid
    {
        get => _isValid;
        private set
        {
            if (SetProperty(ref _isValid, value))
            {
                OnPropertyChanged(nameof(IsInvalid));
            }
        }
    }

    public bool IsInvalid => !IsChecking && !IsValid;

    internal void SetChecking()
    {
        IsChecking = true;
        IsValid = false;
        Text = "Checking…";
        Details = null;
        OnPropertyChanged(nameof(IsInvalid));
    }

    internal void Apply(MediaDependencyValidation validation, bool requiresRestart = false)
    {
        ArgumentNullException.ThrowIfNull(validation);
        IsChecking = false;
        IsValid = validation.IsValid;
        var origin = validation.Origin switch
        {
            MediaDependencyOrigin.Manual => "Manual",
            MediaDependencyOrigin.System => "System",
            MediaDependencyOrigin.Bundled => "Bundled",
            _ => null,
        };
        var version = validation.Version ?? "Valid";
        Text = validation.IsValid
            ? $"{(origin is null ? string.Empty : $"{origin} · ")}{version}{(requiresRestart ? " · restart to apply" : string.Empty)}"
            : validation.Error ?? "Not available";
        Details = validation.ResolvedPath;
        OnPropertyChanged(nameof(IsInvalid));
    }
}
