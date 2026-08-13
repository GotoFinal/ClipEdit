using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    [AvaloniaFact]
    public void Command_bar_progressively_hides_low_priority_content_but_keeps_project_menu()
    {
        var window = new MainWindow();
        var title = window.FindControl<StackPanel>("WorkspaceTitlePanel");
        var projectActions = window.FindControl<StackPanel>("ProjectActionButtons");
        var cropLabel = window.FindControl<TextBlock>("CropPresetLabel");
        var productName = window.FindControl<TextBlock>("ProductNameText");
        var projectMenu = window.FindControl<Button>("ProjectMenuButton");

        Assert.NotNull(title);
        Assert.NotNull(projectActions);
        Assert.NotNull(cropLabel);
        Assert.NotNull(productName);
        Assert.NotNull(projectMenu);

        window.ApplyCommandBarResponsiveLayout(1500);
        Assert.True(title.IsVisible);
        Assert.True(projectActions.IsVisible);
        Assert.True(cropLabel.IsVisible);
        Assert.True(productName.IsVisible);

        window.ApplyCommandBarResponsiveLayout(1180);
        Assert.False(title.IsVisible);
        Assert.False(projectActions.IsVisible);
        Assert.True(cropLabel.IsVisible);
        Assert.True(productName.IsVisible);

        window.ApplyCommandBarResponsiveLayout(1000);
        Assert.False(cropLabel.IsVisible);
        Assert.True(productName.IsVisible);

        window.ApplyCommandBarResponsiveLayout(900);
        Assert.False(productName.IsVisible);
        Assert.True(projectMenu.IsVisible);
        Assert.IsType<MenuFlyout>(projectMenu.Flyout);

        window.Close();
    }

    [AvaloniaFact]
    public void Caption_buttons_explicitly_minimize_and_toggle_maximized_state()
    {
        var window = new MainWindow();
        var minimize = window.FindControl<Button>("MinimizeCaptionButton");
        var maximize = window.FindControl<Button>("MaximizeCaptionButton");
        var maximizeIcon = window.FindControl<PathIcon>("MaximizeCaptionIcon");
        var restoreIcon = window.FindControl<PathIcon>("RestoreCaptionIcon");

        Assert.NotNull(minimize);
        Assert.NotNull(maximize);
        Assert.NotNull(maximizeIcon);
        Assert.NotNull(restoreIcon);

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.False(maximizeIcon.IsVisible);
        Assert.True(restoreIcon.IsVisible);

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(maximizeIcon.IsVisible);
        Assert.False(restoreIcon.IsVisible);

        minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Minimized, window.WindowState);

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
