using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClipEdit.App.Views;

public sealed partial class ConfirmActionDialog : Window
{
    public ConfirmActionDialog()
    {
        InitializeComponent();
    }

    public ConfirmActionDialog(string title, string message, string confirmText)
        : this()
    {
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(false);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(true);
    }
}
