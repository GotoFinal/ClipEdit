using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipEdit.App.ViewModels;
using ClipEdit.App.Settings;
using ClipEdit.App.Platform;
using ClipEdit.App.Updates;
using ClipEdit.App.Views;
using ClipEdit.Media.Analysis;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Analysis;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Frames;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.FFmpeg.Process;
using ClipEdit.Media.Frames;
using ClipEdit.Media.Mpv;
using ClipEdit.Media.Probe;
using ClipEdit.Persistence.Json;

namespace ClipEdit.App;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var applicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipEdit");
            var mediaRuntimeSettingsStore = new MediaRuntimeSettingsStore(
                Path.Combine(applicationDataDirectory, "media-tools.json"));
            var mediaRuntimeSettings = mediaRuntimeSettingsStore.Load();
            var ffprobePath = FfprobeExecutableLocator.Find(
                mediaRuntimeSettings.FfprobePath,
                mediaRuntimeSettings.PreferSystemMediaTools);
            IMediaProbe? mediaProbe = ffprobePath is null
                ? null
                : new FfprobeMediaProbe(ffprobePath);
            var ffmpegPath = FfmpegToolLocator.FindFfmpeg(
                mediaRuntimeSettings.FfmpegPath,
                mediaRuntimeSettings.PreferSystemMediaTools);
            var libMpvPath = MpvNativeLibraryLocator.Find(
                mediaRuntimeSettings.LibMpvPath,
                mediaRuntimeSettings.PreferSystemMediaTools);
            IFrameDecoder? frameDecoder = ffmpegPath is null
                ? null
                : new FfmpegFrameDecoder(ffmpegPath);
            IWaveformRenderer? waveformRenderer = ffmpegPath is null
                ? null
                : new FfmpegWaveformRenderer(ffmpegPath);
            IExportRenderer? exportRenderer = ffmpegPath is null
                ? null
                : new FfmpegExportRenderer(ffmpegPath);
            var recoveryDirectory = Path.Combine(
                applicationDataDirectory,
                "Recovery");
            Directory.CreateDirectory(recoveryDirectory);
            var viewModel = new MainWindowViewModel(
                mediaProbe,
                frameDecoder,
                exportRenderer,
                new JsonProjectStore(),
                recoveryDirectory,
                waveformRenderer: waveformRenderer);
            viewModel.ConfigureMediaRuntime(
                mediaRuntimeSettings,
                ffmpegPath,
                ffprobePath,
                libMpvPath,
                new MediaRuntimeValidator());
            var settingsStore = new CanvasInteractionSettingsStore(
                Path.Combine(applicationDataDirectory, "settings.json"));
            var interactionSettings = settingsStore.Load();
            viewModel.ClipWheelZoomPercent = interactionSettings.WheelZoomPercent;
            viewModel.ClipWheelRotationDegrees = interactionSettings.WheelRotationDegrees;
            viewModel.ClipboardExportMaximumMegabytes =
                interactionSettings.ClipboardExportMaximumMegabytes;
            var hasShownProjectFileAssociationPrompt =
                interactionSettings.HasShownProjectFileAssociationPrompt;
            var exportPreferencesStore = new ExportPreferencesStore(
                Path.Combine(applicationDataDirectory, "export-settings.json"));
            viewModel.ApplyExportPreferences(exportPreferencesStore.Load());
            var releaseAssetId = UpdateViewModel.GetCurrentReleaseAssetId();
            if (releaseAssetId is not null)
            {
                viewModel.ConfigureUpdates(new UpdateViewModel(
                    new GitHubUpdateClient(),
                    new UpdateSettingsStore(Path.Combine(applicationDataDirectory, "updates.json")),
                    releaseAssetId,
                    Path.Combine(applicationDataDirectory, "Updates"),
                    UpdateViewModel.GetCurrentVersion(),
                    SelfUpdateBootstrapper.CanReplaceCurrentExecutable()));
            }
            if (SelfUpdateBootstrapper.StartupError is { } updateError)
            {
                viewModel.Updates.ReportInstallFailure(updateError);
            }
            viewModel.SavedExportPresetsChanged += (_, _) =>
                exportPreferencesStore.Save(viewModel.CreateExportPreferences());
            IProjectFileAssociationService? projectFileAssociationService = null;
            if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } executablePath)
            {
                projectFileAssociationService =
                    new WindowsProjectFileAssociationService(executablePath);
            }

            void SaveInteractionSettings()
            {
                settingsStore.Save(new CanvasInteractionSettings(
                    viewModel.ClipWheelZoomPercent,
                    viewModel.ClipWheelRotationDegrees,
                    viewModel.ClipboardExportMaximumMegabytes,
                    hasShownProjectFileAssociationPrompt));
            }

            var mainWindow = new MainWindow(
                projectFileAssociationService,
                hasShownProjectFileAssociationPrompt,
                () =>
                {
                    hasShownProjectFileAssociationPrompt = true;
                    SaveInteractionSettings();
                })
            {
                DataContext = viewModel,
            };

            mainWindow.Closed += (_, _) =>
            {
                SaveInteractionSettings();
                exportPreferencesStore.Save(viewModel.CreateExportPreferences());
                mediaRuntimeSettingsStore.Save(viewModel.CreateMediaRuntimeSettings());
            };

            desktop.MainWindow = mainWindow;

            var startupArguments = ClassifyStartupArguments(desktop.Args);
            mainWindow.Opened += async (_, _) =>
            {
                if (startupArguments.ProjectPath is not null)
                {
                    await viewModel.OpenProjectWithRelinkingAsync(
                        startupArguments.ProjectPath,
                        discardUnsavedChanges: true);
                }
                else if (startupArguments.MediaPaths.Count > 0)
                {
                    await mainWindow.ImportPathsAsync(startupArguments.MediaPaths);
                }
                else
                {
                    await viewModel.DiscoverRecoveryCandidatesAsync();
                }

                await viewModel.Updates.InitializeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static StartupArguments ClassifyStartupArguments(IEnumerable<string>? arguments)
    {
        var selection = InputPathClassifier.Classify(arguments);
        return new StartupArguments(selection.ProjectPath, selection.MediaPaths);
    }
}

internal sealed record StartupArguments(string? ProjectPath, IReadOnlyList<string> MediaPaths);
