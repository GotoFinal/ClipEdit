using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipEdit.App.ViewModels;
using ClipEdit.App.Views;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.Probe;

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
            var viewModel = new MainWindowViewModel(mediaProbe);
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.MainWindow = mainWindow;

            var initialMediaPaths = desktop.Args?
                .Where(File.Exists)
                .ToArray() ?? [];
            if (initialMediaPaths.Length > 0)
            {
                mainWindow.Opened += async (_, _) =>
                    await mainWindow.ImportPathsAsync(initialMediaPaths);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
