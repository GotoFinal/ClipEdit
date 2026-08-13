using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipEdit.App.ViewModels;
using ClipEdit.App.Settings;
using ClipEdit.App.Views;
using ClipEdit.Media.Analysis;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Analysis;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Frames;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.FFmpeg.Process;
using ClipEdit.Media.Frames;
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
            var ffprobePath = FfprobeExecutableLocator.Find();
            IMediaProbe? mediaProbe = ffprobePath is null
                ? null
                : new FfprobeMediaProbe(ffprobePath);
            var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
            IFrameDecoder? frameDecoder = ffmpegPath is null
                ? null
                : new FfmpegFrameDecoder(ffmpegPath);
            IWaveformRenderer? waveformRenderer = ffmpegPath is null
                ? null
                : new FfmpegWaveformRenderer(ffmpegPath);
            IExportRenderer? exportRenderer = ffmpegPath is null
                ? null
                : new FfmpegExportRenderer(ffmpegPath);
            var applicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipEdit");
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
            var settingsStore = new CanvasInteractionSettingsStore(
                Path.Combine(applicationDataDirectory, "settings.json"));
            var interactionSettings = settingsStore.Load();
            viewModel.ClipWheelZoomPercent = interactionSettings.WheelZoomPercent;
            viewModel.ClipWheelRotationDegrees = interactionSettings.WheelRotationDegrees;
            var exportPreferencesStore = new ExportPreferencesStore(
                Path.Combine(applicationDataDirectory, "export-settings.json"));
            viewModel.ApplyExportPreferences(exportPreferencesStore.Load());
            viewModel.SavedExportPresetsChanged += (_, _) =>
                exportPreferencesStore.Save(viewModel.CreateExportPreferences());
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            mainWindow.Closed += (_, _) =>
            {
                settingsStore.Save(new CanvasInteractionSettings(
                    viewModel.ClipWheelZoomPercent,
                    viewModel.ClipWheelRotationDegrees));
                exportPreferencesStore.Save(viewModel.CreateExportPreferences());
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
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static StartupArguments ClassifyStartupArguments(IEnumerable<string>? arguments)
    {
        var existingPaths = arguments?
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToArray() ?? [];
        var projectPath = existingPaths.FirstOrDefault(path =>
            string.Equals(Path.GetExtension(path), ".clipedit", StringComparison.OrdinalIgnoreCase));
        var mediaPaths = existingPaths
            .Where(path => !string.Equals(
                Path.GetExtension(path),
                ".clipedit",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new StartupArguments(projectPath, mediaPaths);
    }
}

internal sealed record StartupArguments(string? ProjectPath, IReadOnlyList<string> MediaPaths);
