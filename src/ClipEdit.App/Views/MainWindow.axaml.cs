using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClipEdit.App.ViewModels;
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

        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        Closed += OnClosed;
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
            await viewModel.OpenProjectAsync(
                projectPath,
                discardUnsavedChanges: false,
                _lifetimeCancellation.Token);
        }
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
        await LivePreview.TogglePlaybackAsync(_lifetimeCancellation.Token);
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
        ViewModel?.ResetSelectedClipPlacement();
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

    private static void RemoveAudioSelection_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is Control { DataContext: AudioTrackViewModel track })
        {
            track.RemoveSelection();
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
        ViewModel?.ToggleAudioMixer();
    }

    private void FixExportCompatibility_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.MakeExportCropCompatible();
    }

    private async void Export_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (ViewModel is not { CanExport: true } viewModel)
        {
            return;
        }

        var preset = viewModel.SelectedExportPreset;
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
