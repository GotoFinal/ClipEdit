using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ClipEdit.App.Views;

namespace ClipEdit.App.Tests;

public sealed class MainWindowChromeTests
{
    [AvaloniaFact]
    public void Windows_uses_the_command_bar_as_native_chrome_while_other_platforms_keep_decorations()
    {
        var window = new MainWindow();
        var commandBar = window.FindControl<Border>("AppCommandBar");
        var captionButtons = window.FindControl<StackPanel>("WindowsCaptionButtons");

        Assert.NotNull(commandBar);
        Assert.NotNull(captionButtons);
        Assert.Equal(
            WindowDecorationsElementRole.TitleBar,
            WindowDecorationProperties.GetElementRole(commandBar));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
            Assert.True(captionButtons.IsVisible);
            AssertCaptionRole(window, "MinimizeCaptionButton", WindowDecorationsElementRole.MinimizeButton);
            AssertCaptionRole(window, "MaximizeCaptionButton", WindowDecorationsElementRole.MaximizeButton);
            AssertCaptionRole(window, "CloseCaptionButton", WindowDecorationsElementRole.CloseButton);
        }
        else
        {
            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.False(captionButtons.IsVisible);
        }

        window.Close();
    }

    private static void AssertCaptionRole(
        MainWindow window,
        string name,
        WindowDecorationsElementRole expected)
    {
        var button = window.FindControl<Button>(name);
        Assert.NotNull(button);
        Assert.Equal(expected, WindowDecorationProperties.GetElementRole(button));
    }
}
