using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipEdit.App.ViewModels;
using ClipEdit.App.Views;
using ClipEdit.Media.Export;
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
            IExportRenderer? exportRenderer = ffmpegPath is null
                ? null
                : new FfmpegExportRenderer(ffmpegPath);
            var recoveryDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipEdit",
                "Recovery");
            Directory.CreateDirectory(recoveryDirectory);
            var viewModel = new MainWindowViewModel(
                mediaProbe,
                frameDecoder,
                exportRenderer,
                new JsonProjectStore(),
                recoveryDirectory);
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.MainWindow = mainWindow;

            var existingArguments = desktop.Args?
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .ToArray() ?? [];
            var initialProjectPath = existingArguments.FirstOrDefault(path =>
                string.Equals(Path.GetExtension(path), ".clipedit", StringComparison.OrdinalIgnoreCase));
            var initialMediaPaths = existingArguments
                .Where(path => !string.Equals(
                    Path.GetExtension(path),
                    ".clipedit",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var recoveryPath = initialProjectPath is null && initialMediaPaths.Length == 0
                ? FindLatestRecovery(recoveryDirectory)
                : null;
            if (initialProjectPath is not null || recoveryPath is not null || initialMediaPaths.Length > 0)
            {
                mainWindow.Opened += async (_, _) =>
                {
                    if (initialProjectPath is not null)
                    {
                        await viewModel.OpenProjectAsync(
                            initialProjectPath,
                            discardUnsavedChanges: true);
                    }
                    else if (recoveryPath is not null)
                    {
                        await viewModel.RecoverProjectAsync(recoveryPath);
                    }
                    else
                    {
                        await mainWindow.ImportPathsAsync(initialMediaPaths);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? FindLatestRecovery(string recoveryDirectory)
    {
        try
        {
            return Directory
                .EnumerateFiles(recoveryDirectory, "*.recovery.clipedit", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
