using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClipEdit.App.Views;

public enum ClipboardSizeLimitChoice
{
    Cancel,
    CopyAnyway,
    SaveToFile,
}

public sealed partial class ClipboardSizeLimitDialog : Window
{
    public ClipboardSizeLimitDialog()
    {
        InitializeComponent();
    }

    public ClipboardSizeLimitDialog(string fileName, string fileSize, int limitMegabytes)
        : this()
    {
        MessageText.Text =
            $"{fileName} is {fileSize}, above your {limitMegabytes} MB limit. " +
            "Copy it anyway, or save the completed export to a file instead?";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(ClipboardSizeLimitChoice.Cancel);
    }

    private void CopyAnyway_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(ClipboardSizeLimitChoice.CopyAnyway);
    }

    private void SaveToFile_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(ClipboardSizeLimitChoice.SaveToFile);
    }
}
