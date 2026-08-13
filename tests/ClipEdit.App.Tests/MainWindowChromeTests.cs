using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
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
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.True(window.CanResize);
            Assert.True(captionButtons.IsVisible);
            AssertCaptionRole(window, "MinimizeCaptionButton", WindowDecorationsElementRole.MinimizeButton);
            AssertCaptionRole(window, "MaximizeCaptionButton", WindowDecorationsElementRole.MaximizeButton);
            AssertCaptionRole(window, "CloseCaptionButton", WindowDecorationsElementRole.CloseButton);
        }
        else
        {
            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.False(window.ExtendClientAreaToDecorationsHint);
            Assert.False(captionButtons.IsVisible);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Application_brand_uses_the_same_packaged_icon_in_the_window_and_project_menu()
    {
        var window = new MainWindow();
        var projectMenuIcon = window.FindControl<Image>("ProjectMenuIcon");

        Assert.NotNull(window.Icon);
        Assert.NotNull(projectMenuIcon);
        Assert.NotNull(projectMenuIcon.Source);

        window.Close();
    }

    [AvaloniaFact]
    public void Command_bar_buttons_fit_the_compact_thirty_pixel_height()
    {
        var window = new MainWindow();
        var buttonNames = new[]
        {
            "NewProjectButton",
            "OpenProjectButton",
            "SaveProjectButton",
            "ExportButton",
            "CancelExportButton",
        };

        foreach (var buttonName in buttonNames)
        {
            var button = window.FindControl<Button>(buttonName);
            Assert.NotNull(button);
            Assert.Equal(30, button.Height);
            Assert.Equal(new Thickness(9, 4), button.Padding);
            Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Timeline_tool_style_includes_toggle_buttons_without_clipping()
    {
        var window = new MainWindow();
        var laneLayout = window.FindControl<Grid>("TimelineLaneLayout");
        var pointerMode = window.FindControl<ToggleButton>("TimelinePointerModeToggle");
        var snapping = window.FindControl<ToggleButton>("TimelineSnappingToggle");
        var freeMode = window.FindControl<ToggleButton>("TimelineFreeModeToggle");

        Assert.NotNull(laneLayout);
        Assert.NotNull(pointerMode);
        Assert.NotNull(snapping);
        Assert.NotNull(freeMode);
        Assert.Equal(new GridLength(24), laneLayout.RowDefinitions[0].Height);

        foreach (var toggle in new[] { pointerMode, snapping, freeMode })
        {
            Assert.Equal(24, toggle.Height);
            Assert.Equal(new Thickness(5, 0), toggle.Padding);
            Assert.Equal(11, toggle.FontSize);
            Assert.Equal(HorizontalAlignment.Center, toggle.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, toggle.VerticalContentAlignment);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Borderless_windows_expose_valid_resize_edges_only_while_restored()
    {
        var window = new MainWindow();
        var resizeGrips = window.FindControl<Grid>("WindowsResizeGrips");
        var maximize = window.FindControl<Button>("MaximizeCaptionButton");

        Assert.NotNull(resizeGrips);
        Assert.NotNull(maximize);
        Assert.Equal(8, resizeGrips.Children.Count);
        Assert.Equal(OperatingSystem.IsWindows(), resizeGrips.IsVisible);
        Assert.All(resizeGrips.Children, child =>
        {
            var edgeName = Assert.IsType<string>(child.Tag);
            Assert.True(Enum.TryParse<WindowEdge>(edgeName, out _));
        });

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.False(resizeGrips.IsVisible);

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(OperatingSystem.IsWindows(), resizeGrips.IsVisible);

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
    public void Legal_notice_is_available_from_the_persistent_project_menu()
    {
        var window = new MainWindow();
        var projectMenu = window.FindControl<Button>("ProjectMenuButton");

        Assert.NotNull(projectMenu);
        var flyout = Assert.IsType<MenuFlyout>(projectMenu.Flyout);
        var legalItem = flyout.Items
            .OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Legal notices…"));

        Assert.True(legalItem.IsEnabled);
        Assert.Contains("WITHOUT ANY WARRANTY", LegalNoticeDialog.NoticeText);
        Assert.Contains("corresponding-source", LegalNoticeDialog.NoticeText);

        var dialog = new LegalNoticeDialog();
        var notice = dialog.FindControl<TextBox>("NoticeTextBox");
        var openLicenses = dialog.FindControl<Button>("OpenLicensesButton");
        Assert.NotNull(notice);
        Assert.NotNull(openLicenses);
        Assert.True(notice.IsReadOnly);
        Assert.Equal(LegalNoticeDialog.NoticeText, notice.Text);

        dialog.Close();
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
