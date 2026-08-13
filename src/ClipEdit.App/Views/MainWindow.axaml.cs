using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipEdit.App.ViewModels;
using ClipEdit.App.Controls;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Preview;

namespace ClipEdit.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly DataFormat<MediaItemViewModel> VideoClipDataFormat =
        DataFormat.CreateInProcessFormat<MediaItemViewModel>("clipedit-video-clip");

    private static readonly FilePickerFileType MediaFileType = new("Media files")
    {
        Patterns =
        [
            "*.mkv", "*.mp4", "*.mov", "*.webm", "*.avi", "*.wmv", "*.m4v", "*.mpeg", "*.mpg",
            "*.ts", "*.mts", "*.m2ts", "*.flv", "*.ogv", "*.wav", "*.flac", "*.mp3", "*.m4a",
            "*.aac", "*.opus", "*.ogg", "*.wma",
        ],
    };

    private static readonly FilePickerFileType ProjectFileType = new("ClipEdit projects")
    {
        Patterns = ["*.clipedit"],
    };

    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            WindowDecorations = WindowDecorations.None;
            ExtendClientAreaToDecorationsHint = true;
            WindowsCaptionButtons.IsVisible = true;
        }

        ApplyCommandBarResponsiveLayout(Width);
        UpdateCaptionButtonState();

        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        SizeChanged += OnWindowSizeChanged;
        Closed += OnClosed;
    }

    internal void ApplyCommandBarResponsiveLayout(double width)
    {
        WorkspaceTitlePanel.IsVisible = width >= 1420;
        ProjectActionButtons.IsVisible = width >= 1190;
        CropPresetLabel.IsVisible = width >= 1050;
        ProductNameText.IsVisible = width >= 930;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        _ = sender;
        ApplyCommandBarResponsiveLayout(eventArgs.NewSize.Width);
        UpdateCaptionButtonState();
    }

    private async void LegalNotices_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await new LegalNoticeDialog().ShowDialog(this);
    }

    private void AppCommandBar_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        _ = sender;
        if (!OperatingSystem.IsWindows() || IsInteractiveCommandBarSource(eventArgs.Source))
        {
            return;
        }

        var point = eventArgs.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximized();
        }
        else
        {
            BeginMoveDrag(eventArgs);
        }

        eventArgs.Handled = true;
    }

    private void WindowResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!OperatingSystem.IsWindows() ||
            WindowState == WindowState.Maximized ||
            sender is not Control { Tag: string edgeName } ||
            !Enum.TryParse<WindowEdge>(edgeName, out var edge) ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResizeDrag(edge, eventArgs);
        eventArgs.Handled = true;
    }

    private bool IsInteractiveCommandBarSource(object? source)
    {
        for (var current = source as Visual;
             current is not null && !ReferenceEquals(current, AppCommandBar);
             current = current.GetVisualParent())
        {
            if (current is Button or ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    private void MinimizeCaptionButton_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        WindowState = WindowState.Minimized;
    }

    private void MaximizeCaptionButton_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ToggleMaximized();
    }

    private void CloseCaptionButton_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close();
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateCaptionButtonState();
    }

    private void UpdateCaptionButtonState()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowsResizeGrips.IsVisible = OperatingSystem.IsWindows() && !isMaximized;
        MaximizeCaptionIcon.IsVisible = !isMaximized;
        RestoreCaptionIcon.IsVisible = isMaximized;
        ToolTip.SetTip(MaximizeCaptionButton, isMaximized ? "Restore" : "Maximize");
    }

    public Task ImportPathsAsync(IEnumerable<string> sourcePaths)
    {
        return ViewModel?.ImportFilesAsync(sourcePaths, _lifetimeCancellation.Token) ?? Task.CompletedTask;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void NewProject_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanNewProject: true } viewModel)
        {
            return;
        }

        if (viewModel.IsProjectDirty)
        {
            var confirmation = new ConfirmActionDialog(
                "Create a new project?",
                "The current project has unsaved changes. Discard them and start a new empty project? Source media files will not be changed.",
                "Discard and create new");
            if (!await confirmation.ShowDialog<bool>(this))
            {
                return;
            }
        }

        await viewModel.NewProjectAsync(
            discardUnsavedChanges: true,
            _lifetimeCancellation.Token);
    }

    private async void OpenMedia_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Add media to ClipEdit",
                AllowMultiple = true,
                FileTypeFilter = [MediaFileType, FilePickerFileTypes.All],
            });

        var paths = files
            .Select(file => file.Path)
            .Where(uri => uri.IsFile)
            .Select(uri => uri.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        await ImportPathsAsync(paths);
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanOpenProject: true } viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open ClipEdit project",
                AllowMultiple = false,
                FileTypeFilter = [ProjectFileType, FilePickerFileTypes.All],
            });
        var projectUri = files
            .Select(file => file.Path)
            .FirstOrDefault(uri => uri.IsFile);
        var projectPath = projectUri?.LocalPath;
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            await viewModel.OpenProjectWithRelinkingAsync(
                projectPath,
                discardUnsavedChanges: false,
                cancellationToken: _lifetimeCancellation.Token);
        }
    }

    private async void RecoverCandidate_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not Button { DataContext: RecoveryCandidateViewModel { CanRecover: true } candidate } ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        await viewModel.OpenProjectWithRelinkingAsync(
            candidate.RecoveryPath,
            isRecovery: true,
            discardUnsavedChanges: true,
            cancellationToken: _lifetimeCancellation.Token);
    }

    private async void DiscardRecoveryCandidate_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not Button { DataContext: RecoveryCandidateViewModel candidate } ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        var confirmation = new ConfirmActionDialog(
            "Discard this recovery autosave?",
            "The autosaved edit decisions will be removed. Referenced source media files will not be changed.",
            "Discard autosave");
        if (await confirmation.ShowDialog<bool>(this))
        {
            await viewModel.DiscardRecoveryAsync(candidate, _lifetimeCancellation.Token);
        }
    }

    private async void RelinkMissingMedia_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not Button { DataContext: MissingMediaReferenceViewModel reference } ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = $"Relink {reference.DisplayName}",
                AllowMultiple = false,
                FileTypeFilter = [MediaFileType, FilePickerFileTypes.All],
            });
        var replacementPath = files
            .Select(file => file.Path)
            .FirstOrDefault(uri => uri.IsFile)?
            .LocalPath;
        if (!string.IsNullOrWhiteSpace(replacementPath))
        {
            await viewModel.RelinkMissingMediaAsync(
                reference,
                replacementPath,
                _lifetimeCancellation.Token);
        }
    }

    private void CancelPendingProjectOpen_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.CancelPendingProjectOpen();
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanSaveProject: true } viewModel)
        {
            return;
        }

        if (viewModel.ProjectPath is not null)
        {
            await viewModel.SaveProjectAsync(
                cancellationToken: _lifetimeCancellation.Token);
            return;
        }

        await SaveProjectAsAsync(viewModel);
    }

    private async Task SaveProjectAsAsync(MainWindowViewModel viewModel)
    {
        var destination = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save ClipEdit project",
                SuggestedFileName = "Untitled.clipedit",
                DefaultExtension = "clipedit",
                ShowOverwritePrompt = true,
                FileTypeChoices = [ProjectFileType],
            });
        if (destination?.Path is not { IsFile: true } destinationUri)
        {
            return;
        }

        var projectPath = destinationUri.LocalPath;
        if (!projectPath.EndsWith(".clipedit", StringComparison.OrdinalIgnoreCase))
        {
            projectPath = Path.ChangeExtension(projectPath, ".clipedit");
        }

        await viewModel.SaveProjectAsync(projectPath, _lifetimeCancellation.Token);
    }

    private static void OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        _ = sender;
        eventArgs.DragEffects = eventArgs.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void ResetCanvasControls_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ResetCanvasInteractionSettings();
    }

    private void UseAutoTool_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.UseAutoTool();
    }

    private async void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        _ = sender;
        eventArgs.Handled = true;

        var paths = eventArgs.DataTransfer
            .TryGetFiles()
            .OrEmpty()
            .Select(file => file.Path)
            .Where(uri => uri.IsFile)
            .Select(uri => uri.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        await ImportPathsAsync(paths);
    }

    private void GoToStart_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is { } viewModel)
        {
            viewModel.SequencePlayheadSeconds = 0;
        }
    }

    private async void TogglePlayback_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (LivePreview.IsPaused && ViewModel?.PrepareSequencePlayback() is { } sourcePosition)
        {
            SynchronizeLivePreviewToSelectedClip(sourcePosition);
        }

        await LivePreview.TogglePlaybackAsync(_lifetimeCancellation.Token);
    }

    private async void LivePreview_PlaybackCompleted(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel?.TryAdvanceSequencePlayback() != true ||
            ViewModel.PrepareSequencePlayback() is not { } sourcePosition)
        {
            return;
        }

        SynchronizeLivePreviewToSelectedClip(sourcePosition);
        try
        {
            await LivePreview.PlayAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown superseded the automatic clip transition.
        }
    }

    private void SynchronizeLivePreviewToSelectedClip(MediaTime sourcePosition)
    {
        if (ViewModel?.SelectedVideoClip is not { } clip)
        {
            return;
        }

        LivePreview.SetCurrentValue(MpvVideoView.SourcePathProperty, clip.SourcePath);
        LivePreview.SetCurrentValue(MpvVideoView.PositionProperty, sourcePosition);
        LivePreview.SetCurrentValue(MpvVideoView.PlaybackRangesProperty, clip.PlaybackRanges);
        LivePreview.SetCurrentValue(MpvVideoView.SourceVideoSizeProperty, clip.VideoSize);
        LivePreview.SetCurrentValue(MpvVideoView.CanvasTransformProperty, clip.CanvasTransform);
    }

    private async void StepFrameBackward_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await LivePreview.StepFrameAsync(
            PreviewFrameStepDirection.Backward,
            _lifetimeCancellation.Token);
    }

    private async void StepFrameForward_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await LivePreview.StepFrameAsync(
            PreviewFrameStepDirection.Forward,
            _lifetimeCancellation.Token);
    }

    private void GoToEnd_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is { } viewModel)
        {
            viewModel.SequencePlayheadSeconds = viewModel.SequenceDurationSeconds;
        }
    }

    private void MarkIn_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MarkSequenceSelectionStart();
    }

    private void MarkOut_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MarkSequenceSelectionEnd();
    }

    private void RemoveSelection_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.RemoveSequenceSelection();
    }

    private void KeepSelectionOnly_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.KeepSequenceSelectionOnly();
    }

    private void ResetCuts_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ResetSequenceCuts();
    }

    private void ResetCrop_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ResetCanvasCrop();
    }

    private void ApplyCropPreset_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ApplyCropPresetToSelected();
    }

    private void ApplyCropPresetToAll_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ApplyCropPresetToAllVideos();
    }

    private void UseCropTool_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.UseCropTool();
    }

    private void UseTransformTool_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.UseTransformTool();
    }

    private void FillClipCanvas_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ResetSelectedClipToFill();
    }

    private void FitClipCanvas_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.FitSelectedClipToCanvas();
    }

    private void MoveVideoLeft_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MoveSelectedVideoLeft();
    }

    private void MoveVideoRight_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MoveSelectedVideoRight();
    }

    private async void VideoClipDragHandle_PointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: MediaItemViewModel source } ||
            ViewModel is not { } viewModel ||
            !eventArgs.GetCurrentPoint((Control)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        viewModel.SelectedMedia = source;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(VideoClipDataFormat, source));
        eventArgs.Handled = true;
        await DragDrop.DoDragDropAsync(eventArgs, transfer, DragDropEffects.Move);
    }

    private static void VideoClip_DragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = sender is Control { DataContext: MediaItemViewModel } &&
                                eventArgs.DataTransfer.Contains(VideoClipDataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void VideoClip_Drop(object? sender, DragEventArgs eventArgs)
    {
        if (sender is Control { DataContext: MediaItemViewModel target } control &&
            ViewModel is { } viewModel &&
            eventArgs.DataTransfer.TryGetValue(VideoClipDataFormat) is { } source)
        {
            var insertAfter = eventArgs.GetPosition(control).X >= control.Bounds.Width / 2;
            viewModel.ReorderVideoClip(source, target, insertAfter);
            eventArgs.DragEffects = DragDropEffects.Move;
        }
        else
        {
            eventArgs.DragEffects = DragDropEffects.None;
        }

        eventArgs.Handled = true;
    }

    private void TimelineZoomOut_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ZoomSequenceTimeline(0.5);
    }

    private void TimelineZoomIn_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.ZoomSequenceTimeline(2);
    }

    private void TimelineFit_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.FitSequenceTimeline();
    }

    private void SequenceTimeline_PointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        _ = sender;
        var pointer = eventArgs.GetPosition(this);
        var maximumLeft = Math.Max(4, Bounds.Width - TimelineHoverPreview.Width - 4);
        var maximumTop = Math.Max(4, Bounds.Height - TimelineHoverPreview.Height - 4);
        var left = Math.Clamp(pointer.X + 14, 4, maximumLeft);
        var top = Math.Clamp(pointer.Y - TimelineHoverPreview.Height - 10, 4, maximumTop);
        TimelineHoverPreview.Margin = new Thickness(left, top, 0, 0);
    }

    private void SplitClip_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SplitSelectedVideoClip();
    }

    private void DeleteSelectedClip_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.DeleteSelectedVideoClip();
    }

    private void SequenceTimeline_DeleteRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.DeleteSelectedVideoClip();
    }

    private void SequenceTimeline_ClipMoveRequested(object? sender, VideoClipMoveRequestedEventArgs eventArgs)
    {
        _ = sender;
        ViewModel?.MoveVideoClipTo(eventArgs.Clip, eventArgs.TimelineStart);
    }


    private void SequenceTimeline_SplitRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SplitSelectedVideoClip();
    }

    private void SequenceTimeline_MoveLeftRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MoveSelectedVideoLeft();
    }

    private void SequenceTimeline_MoveRightRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MoveSelectedVideoRight();
    }

    private async void RemoveSelectedMedia_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanRemoveSelectedMedia: true, SelectedMedia: { } selected } viewModel)
        {
            return;
        }

        var confirmation = new ConfirmActionDialog(
            "Remove media from project?",
            $"Remove {selected.DisplayName} and its edits from this project? The source file will stay untouched.",
            "Remove from project");
        if (await confirmation.ShowDialog<bool>(this))
        {
            viewModel.RemoveSelectedMedia();
        }
    }

    private async void ProjectMedia_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.Key != Key.Delete ||
            ViewModel is not { CanRemoveSelectedMedia: true, SelectedMedia: { } selected } viewModel)
        {
            return;
        }

        eventArgs.Handled = true;
        var confirmation = new ConfirmActionDialog(
            "Remove media from project?",
            $"Remove {selected.DisplayName}, every timeline instance, and its edits from this project? The source file will stay untouched.",
            "Remove from project");
        if (await confirmation.ShowDialog<bool>(this))
        {
            viewModel.RemoveSelectedMedia();
        }
    }

    private void ProjectMediaItem_DoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: MediaItemViewModel mediaItem } &&
            ViewModel?.AddMediaToTimeline(mediaItem) == true)
        {
            SequenceTimeline.Focus();
            eventArgs.Handled = true;
        }
    }

    private void SequenceTimeline_CopyRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.CopySelectedVideoClip();
    }

    private void SequenceTimeline_PasteRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.PasteVideoClip();
    }

    private static void RemoveAudioSelection_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.SilenceTimelineSelection();
        }
    }

    private static void AudioTimeline_DeleteRequested(object? sender, EventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.SilenceTimelineSelection();
        }
    }

    private static void ResetAudioTrack_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.Reset();
        }
    }

    private void ToggleAudioClipMembership_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            ViewModel?.ToggleSelectedClipAudioMembership(track);
        }
    }

    private void RemoveAudioTrack_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            ViewModel?.RemoveAudioTrack(track);
        }
    }

    private void RestoreAudioTracks_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.RestoreMissingAudioTracks();
    }

    private static void AudioTimelineZoomOut_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.ZoomTimeline(0.5);
        }
    }

    private static void AudioTimelineZoomIn_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.ZoomTimeline(2);
        }
    }

    private static void AudioTimelineFit_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.FitTimeline();
        }
    }

    private void ToggleAudioMixer_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.ToggleAudioMixer();
        if (viewModel.ShowAudioMixer)
        {
            Dispatcher.UIThread.Post(AudioMixerPanel.BringIntoView, DispatcherPriority.Loaded);
        }
    }

    private void FixExportCompatibility_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MakeExportCropCompatible();
    }

    private void SaveExportPreset_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SaveCustomExportPreset();
    }

    private void LoadExportPreset_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.LoadSelectedCustomExportPreset();
    }

    private void DeleteExportPreset_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.DeleteSelectedCustomExportPreset();
    }

    private async void Export_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanExport: true } viewModel)
        {
            return;
        }

        var preset = viewModel.GetEffectiveExportPreset();
        var destination = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export clip",
                SuggestedFileName = viewModel.GetSuggestedExportFileName(),
                DefaultExtension = preset.FileExtension.TrimStart('.'),
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(preset.DisplayName)
                    {
                        Patterns = [$"*{preset.FileExtension}"],
                    },
                ],
            });
        if (destination?.Path is not { IsFile: true } destinationUri)
        {
            return;
        }

        var destinationPath = destinationUri.LocalPath;
        if (!destinationPath.EndsWith(preset.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            destinationPath = Path.ChangeExtension(destinationPath, preset.FileExtension);
        }

        await viewModel.ExportAsync(
            destinationPath,
            replaceExistingDestination: File.Exists(destinationPath),
            _lifetimeCancellation.Token);
    }

    private void CancelExport_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.CancelExport();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _lifetimeCancellation.Cancel();
        _ = LivePreview.ShutdownAsync();
        ViewModel?.Dispose();
        _lifetimeCancellation.Dispose();
    }
}

file static class StorageItemCollectionExtensions
{
    public static IEnumerable<IStorageItem> OrEmpty(this IReadOnlyList<IStorageItem>? items)
    {
        return items ?? [];
    }
}
