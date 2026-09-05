using Avalonia;
using ClipEdit.App.Updates;
using ClipEdit.App.Diagnostics;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (SelfUpdateBootstrapper.TryRunUpdateHelper(args, out var updateExitCode))
        {
            return updateExitCode;
        }

        using var log = new SessionLog(SessionLog.FilePath);
        MediaProcessDiagnostics.Sink = log.Write;
        UnhandledExceptionEventHandler unhandled = (_, error) => log.Write($"Unhandled exception: {error.ExceptionObject}");
        AppDomain.CurrentDomain.UnhandledException += unhandled;
        try
        {
            args = SelfUpdateBootstrapper.PrepareApplicationArguments(args);
            BundledRuntimeBootstrapper.Prepare(AppContext.BaseDirectory);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            log.Write($"Application failed: {exception}");
            throw;
        }
        finally
        {
            log.Write("Application stopped.");
            AppDomain.CurrentDomain.UnhandledException -= unhandled;
            MediaProcessDiagnostics.Sink = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect();
    }
}
