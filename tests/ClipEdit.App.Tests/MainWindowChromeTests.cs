using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClipEdit.Application.Export;
using ClipEdit.App.Views;
using ClipEdit.App.Platform;
using ClipEdit.App.ViewModels;
using ClipEdit.Media.Export;

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
            "UpdateButton",
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
    public void Update_controls_keep_beta_releases_opt_in()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(mediaProbe: null),
        };
        var updateButton = window.FindControl<Button>("UpdateButton");
        var toolbarUpdateHost = window.FindControl<Panel>("ToolbarUpdateHost");
        var welcomeUpdateButton = window.FindControl<Button>("WelcomeUpdateButton");
        var settingsUpdateButton = window.FindControl<Button>("SettingsUpdateButton");
        var settingsActionButtons = window.FindControl<StackPanel>("UpdateSettingsActionButtons");
        var checkForUpdatesButton = window.FindControl<Button>("CheckForUpdatesButton");
        var automaticChecks = window.FindControl<CheckBox>("AutomaticUpdateChecksCheckBox");
        var includeBeta = window.FindControl<CheckBox>("IncludeBetaVersionsCheckBox");

        Assert.NotNull(updateButton);
        Assert.NotNull(toolbarUpdateHost);
        Assert.NotNull(welcomeUpdateButton);
        Assert.NotNull(settingsUpdateButton);
        Assert.NotNull(settingsActionButtons);
        Assert.NotNull(checkForUpdatesButton);
        Assert.NotNull(automaticChecks);
        Assert.NotNull(includeBeta);
        Assert.False(includeBeta.IsChecked);
        Assert.False(toolbarUpdateHost.IsVisible);
        Assert.False(updateButton.IsVisible);
        Assert.False(welcomeUpdateButton.IsVisible);
        Assert.Equal(Orientation.Horizontal, settingsActionButtons.Orientation);
        Assert.Equal(2, settingsActionButtons.Children.Count);
        Assert.Same(checkForUpdatesButton, settingsActionButtons.Children[0]);
        Assert.Same(settingsUpdateButton, settingsActionButtons.Children[1]);

        window.Close();
    }

    [AvaloniaFact]
    public void Settings_are_grouped_and_recovery_limits_are_editable()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        var interaction = window.FindControl<Expander>("InteractionSettingsExpander");
        var recovery = window.FindControl<Expander>("RecoverySettingsExpander");
        var experimental = window.FindControl<Expander>("ExperimentalSettingsExpander");
        var mediaRuntime = window.FindControl<Expander>("MediaRuntimeSettingsExpander");
        var updates = window.FindControl<Expander>("UpdateSettingsExpander");
        var retentionDays = window.FindControl<NumericUpDown>("RecoveryRetentionDaysInput");
        var maximumFiles = window.FindControl<NumericUpDown>("MaximumRecoveryFilesInput");
        var boundaryGop = window.FindControl<CheckBox>("BoundaryGopRenderingCheckBox");

        Assert.NotNull(interaction);
        Assert.NotNull(recovery);
        Assert.NotNull(experimental);
        Assert.NotNull(mediaRuntime);
        Assert.NotNull(updates);
        Assert.NotNull(retentionDays);
        Assert.NotNull(maximumFiles);
        Assert.NotNull(boundaryGop);
        Assert.True(interaction.IsExpanded);
        Assert.False(recovery.IsExpanded);
        Assert.False(experimental.IsExpanded);
        Assert.False(mediaRuntime.IsExpanded);
        Assert.False(updates.IsExpanded);
        Assert.Equal(365, retentionDays.Maximum);
        Assert.Equal(200, maximumFiles.Maximum);
        Assert.Equal(7, viewModel.RecoveryRetentionDays);
        Assert.Equal(20, viewModel.MaximumRecoveryFiles);
        Assert.False(boundaryGop.IsChecked);

        window.Close();
    }

    [AvaloniaFact]
    public void Encoder_combo_keeps_a_visible_selection_when_the_codec_probe_updates_options()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        var exportHost = HostExportSettings(window, viewModel);
        var encoder = window.FindControl<ComboBox>("ExportVideoEncoderComboBox");
        Assert.NotNull(encoder);
        Assert.Same(viewModel.SelectedExportVideoEncoder, encoder.SelectedItem);
        var originalItemsSource = encoder.ItemsSource;
        var originalChoices = viewModel.ExportVideoEncoderChoices.ToArray();

        viewModel.ConfigureExportHardwareCapabilityProbe(new SoftwareCodecProbe());
        viewModel.SelectedExportPreset = BuiltInExportPresets.Custom;
        foreach (var codec in new[]
                 {
                     VideoCodecChoice.Hevc,
                     VideoCodecChoice.Av1,
                     VideoCodecChoice.H264,
                 })
        {
            viewModel.CustomVideoCodec = codec;

            Assert.Same(originalItemsSource, encoder.ItemsSource);
            Assert.Equal(originalChoices.Length, viewModel.ExportVideoEncoderChoices.Count);
            for (var index = 0; index < originalChoices.Length; index++)
            {
                Assert.Same(originalChoices[index], viewModel.ExportVideoEncoderChoices[index]);
            }
            Assert.NotNull(encoder.SelectedItem);
            Assert.Same(viewModel.SelectedExportVideoEncoder, encoder.SelectedItem);
            Assert.Contains(encoder.SelectedItem, viewModel.ExportVideoEncoderChoices);
        }
        exportHost.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Dependent_codec_combos_keep_visible_selections_when_container_changes()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        var exportHost = HostExportSettings(window, viewModel);
        var videoCodec = window.FindControl<ComboBox>("CustomVideoCodecComboBox");
        var audioCodec = window.FindControl<ComboBox>("CustomAudioCodecComboBox");
        Assert.NotNull(videoCodec);
        Assert.NotNull(audioCodec);

        foreach (var container in new[]
                 {
                     ExportContainerChoice.WebM,
                     ExportContainerChoice.Matroska,
                     ExportContainerChoice.Gif,
                     ExportContainerChoice.Mp4,
                 })
        {
            viewModel.CustomExportContainer = container;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            Assert.True(
                videoCodec.SelectedItem is not null,
                $"{container.DisplayName}: video SelectedValue={videoCodec.SelectedValue ?? "<null>"}, " +
                $"view-model value={viewModel.CustomVideoCodec.Value}, " +
                $"items={string.Join(",", viewModel.CustomVideoCodecChoices.Select(choice => choice.Value))}");
            Assert.True(
                audioCodec.SelectedItem is not null,
                $"{container.DisplayName}: audio SelectedValue={audioCodec.SelectedValue ?? "<null>"}, " +
                $"view-model value={viewModel.CustomAudioCodec.Value}, " +
                $"items={string.Join(",", viewModel.CustomAudioCodecChoices.Select(choice => choice.Value))}");
            Assert.Same(viewModel.CustomVideoCodec, videoCodec.SelectedItem);
            Assert.Same(viewModel.CustomAudioCodec, audioCodec.SelectedItem);
            if (container == ExportContainerChoice.Matroska)
            {
                videoCodec.SelectedItem = VideoCodecChoice.Vp9;
                audioCodec.SelectedItem = AudioCodecChoice.Flac;
                Assert.Same(VideoCodecChoice.Vp9, viewModel.CustomVideoCodec);
                Assert.Same(AudioCodecChoice.Flac, viewModel.CustomAudioCodec);
            }
        }

        exportHost.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Media_runtime_controls_expose_system_preference_and_manual_paths()
    {
        var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        var preferSystem = window.FindControl<CheckBox>("PreferSystemMediaToolsCheckBox");
        var ffmpegPath = window.FindControl<TextBox>("FfmpegPathTextBox");
        var ffprobePath = window.FindControl<TextBox>("FfprobePathTextBox");
        var libMpvPath = window.FindControl<TextBox>("LibMpvPathTextBox");
        var pickFfmpeg = window.FindControl<Button>("PickFfmpegPathButton");
        var pickFfprobe = window.FindControl<Button>("PickFfprobePathButton");
        var pickLibMpv = window.FindControl<Button>("PickLibMpvPathButton");

        Assert.NotNull(preferSystem);
        Assert.NotNull(ffmpegPath);
        Assert.NotNull(ffprobePath);
        Assert.NotNull(libMpvPath);
        Assert.NotNull(pickFfmpeg);
        Assert.NotNull(pickFfprobe);
        Assert.NotNull(pickLibMpv);
        Assert.True(viewModel.PreferSystemMediaTools);

        window.Close();
    }

    [AvaloniaFact]
    public void Export_settings_are_a_narrow_joined_sub_button()
    {
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        viewModel.SelectedExportQuality = ExportQualityChoice.MatchSource;
        var export = window.FindControl<Button>("ExportButton");
        var settings = window.FindControl<Button>("ExportSettingsButton");
        var qualityMode = window.FindControl<Grid>("VideoQualityModePanel");
        var matchQuality = window.FindControl<ToggleButton>("MatchInputQualityButton");
        var customQuality = window.FindControl<ToggleButton>("CustomQualityButton");
        var bitRateQuality = window.FindControl<ToggleButton>("BitRateQualityButton");
        var customQualityPanel = window.FindControl<Grid>("CustomQualityPanel");
        var bitRateInput = window.FindControl<NumericUpDown>("ExportVideoBitRateInput");
        var remember = window.FindControl<CheckBox>("RememberExportAdjustmentsCheckBox");
        var exportMethod = window.FindControl<Border>("ExportMethodPanel");
        var fastCopySettings = window.FindControl<Button>("ApplyFastCopySettingsButton");

        Assert.NotNull(export);
        Assert.NotNull(settings);
        Assert.NotNull(qualityMode);
        Assert.NotNull(matchQuality);
        Assert.NotNull(customQuality);
        Assert.NotNull(bitRateQuality);
        Assert.NotNull(customQualityPanel);
        Assert.NotNull(bitRateInput);
        Assert.NotNull(remember);
        Assert.NotNull(exportMethod);
        Assert.NotNull(fastCopySettings);
        Assert.Equal(30, export.Height);
        Assert.Equal(30, settings.Height);
        Assert.Equal(24, settings.Width);
        Assert.True(settings.IsEnabled);
        var flyout = Assert.IsType<Flyout>(settings.Flyout);
        Assert.True(flyout.OverlayDismissEventPassThrough);
        Assert.True(viewModel.UsesMatchedInputQuality);
        Assert.False(viewModel.UsesCustomExportQuality);
        Assert.False(viewModel.UsesTargetBitRate);

        bitRateQuality.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(ExportQualityMode.BitRate, viewModel.ExportQualityMode);
        Assert.True(viewModel.UsesTargetBitRate);
        Assert.True(bitRateInput.IsVisible);

        viewModel.SelectedExportPreset = BuiltInExportPresets.Gif;

        Assert.True(viewModel.UsesCustomExportQuality);
        Assert.False(viewModel.UsesMatchedInputQuality);
        Assert.False(viewModel.UsesTargetBitRate);

        window.Close();
    }

    [AvaloniaFact]
    public void Preview_and_edit_panes_share_a_constrained_drag_splitter()
    {
        var window = new MainWindow();
        var workspace = window.FindControl<Grid>("QuickWorkspaceGrid");
        var splitter = window.FindControl<GridSplitter>("WorkspacePaneSplitter");
        var fastCuts = window.FindControl<ToggleButton>("FastCutModeToggle");

        Assert.NotNull(workspace);
        Assert.NotNull(splitter);
        Assert.NotNull(fastCuts);
        Assert.Equal(3, workspace.RowDefinitions.Count);
        Assert.Equal(new GridLength(5), workspace.RowDefinitions[1].Height);
        Assert.Equal(220, workspace.RowDefinitions[0].MinHeight);
        Assert.Equal(160, workspace.RowDefinitions[2].MinHeight);
        Assert.Equal(1, Grid.GetRow(splitter));
        Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
        Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
        Assert.False(splitter.ShowsPreview);

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
    public void Dragging_the_custom_title_bar_restores_a_maximized_window_and_resize_grips()
    {
        var window = new MainWindow();
        var resizeGrips = window.FindControl<Grid>("WindowsResizeGrips");

        Assert.NotNull(resizeGrips);
        window.WindowState = WindowState.Maximized;

        window.PrepareForTitleBarMoveDrag();

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
    public void Project_menu_exposes_undo_and_redo_hotkeys()
    {
        var window = new MainWindow();
        var undo = window.FindControl<MenuItem>("UndoMenuItem");
        var redo = window.FindControl<MenuItem>("RedoMenuItem");

        Assert.NotNull(undo);
        Assert.NotNull(redo);
        Assert.Equal(new KeyGesture(Key.Z, KeyModifiers.Control), undo.HotKey);
        Assert.Equal(new KeyGesture(Key.Y, KeyModifiers.Control), redo.HotKey);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_routes_control_z_and_control_y_to_project_history()
    {
        var viewModel = new MainWindowViewModel(null);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        var keyTarget = window.FindControl<Button>("ExportButton");
        Assert.NotNull(keyTarget);
        window.Show();
        viewModel.SelectedExportPreset = BuiltInExportPresets.WebM;
        Assert.True(viewModel.CanUndo);

        var undo = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Z,
            KeyModifiers = KeyModifiers.Control,
        };
        keyTarget.RaiseEvent(undo);

        Assert.True(undo.Handled);
        Assert.Equal(BuiltInExportPresets.Mp4Compatible, viewModel.SelectedExportPreset);
        Assert.True(viewModel.CanRedo);

        var redo = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Y,
            KeyModifiers = KeyModifiers.Control,
        };
        keyTarget.RaiseEvent(redo);

        Assert.True(redo.Handled);
        Assert.Equal(BuiltInExportPresets.WebM, viewModel.SelectedExportPreset);
        window.Close();
    }

    [AvaloniaFact]
    public void Project_menu_exposes_windows_file_association_setup_when_available()
    {
        var association = new FakeProjectFileAssociationService();
        using var viewModel = new MainWindowViewModel(mediaProbe: null);
        var window = new MainWindow(
            association,
            hasShownProjectFileAssociationPrompt: true,
            markProjectFileAssociationPromptShown: null)
        {
            DataContext = viewModel,
        };
        var menuItem = window.FindControl<MenuItem>("RegisterProjectFileAssociationMenuItem");

        Assert.NotNull(menuItem);
        Assert.True(menuItem.IsVisible);
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, association.RegisterCount);
        Assert.Equal("Association registered for this test", viewModel.StatusText);

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

    private static Window HostExportSettings(MainWindow window, MainWindowViewModel viewModel)
    {
        var button = window.FindControl<Button>("ExportSettingsButton");
        Assert.NotNull(button);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        flyout.Content = null;
        var host = new Window
        {
            DataContext = viewModel,
            Content = content,
        };
        host.Show();
        return host;
    }

    private sealed class FakeProjectFileAssociationService : IProjectFileAssociationService
    {
        public int RegisterCount { get; private set; }

        public ProjectFileAssociationResult Register()
        {
            RegisterCount++;
            return new ProjectFileAssociationResult(true, "Association registered for this test");
        }
    }

    private sealed class SoftwareCodecProbe : IExportHardwareCapabilityProbe
    {
        public Task<ExportHardwareCapabilities> ProbeAsync(
            VideoCodecFamily videoCodec,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExportHardwareCapabilities(
            [
                new(
                    ExportVideoEncoder.Software,
                    videoCodec == VideoCodecFamily.Av1 ? "Software (libaom-av1)" : "Software",
                    true,
                    "Available",
                    VideoCodec: videoCodec),
            ]));
    }
}
