using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ClipEdit.App.Tests.HeadlessTestAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ClipEdit.App.Tests;

public sealed class HeadlessTestApplication : Avalonia.Application;

public static class HeadlessTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<HeadlessTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
