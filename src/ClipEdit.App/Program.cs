using Avalonia;
using ClipEdit.App.Updates;

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

        args = SelfUpdateBootstrapper.PrepareApplicationArguments(args);
        BundledRuntimeBootstrapper.Prepare(AppContext.BaseDirectory);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect();
    }
}
