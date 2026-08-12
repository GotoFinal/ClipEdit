using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClipEdit.App.ViewModels;
using ClipEdit.Media.Preview;

namespace ClipEdit.App.Views;

public sealed partial class MainWindow : Window
{
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
        if (ViewModel?.SelectedMedia is { } media)
        {
            media.PlayheadSeconds = 0;
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
        if (ViewModel?.SelectedMedia is { } media)
        {
            media.PlayheadSeconds = media.SourceDurationSeconds;
        }
    }

    private void MarkIn_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.MarkSelectionStart();
    }

    private void MarkOut_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.MarkSelectionEnd();
    }

    private void RemoveSelection_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.RemoveSelection();
    }

    private void KeepSelectionOnly_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.KeepSelectionOnly();
    }

    private void ResetCuts_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.ResetCuts();
    }

    private void ResetCrop_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.ResetCrop();
    }

    private void TimelineZoomOut_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.ZoomTimeline(0.5);
    }

    private void TimelineZoomIn_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.ZoomTimeline(2);
    }

    private void TimelineFit_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ViewModel?.SelectedMedia?.FitTimeline();
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
