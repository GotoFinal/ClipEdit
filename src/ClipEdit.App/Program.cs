using Avalonia;

namespace ClipEdit.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
