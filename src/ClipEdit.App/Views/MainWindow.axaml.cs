using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClipEdit.App.ViewModels;

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

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _lifetimeCancellation.Cancel();
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
