using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using ClipEdit.App.Views;

namespace ClipEdit.App.Tests;

public sealed class ClipboardSizeLimitDialogTests
{
    [AvaloniaFact]
    public void Dialog_explains_the_completed_export_and_exposes_all_recovery_actions()
    {
        var dialog = new ClipboardSizeLimitDialog("clip.mp4", "148.5 MB", 100);
        var message = dialog.FindControl<TextBlock>("MessageText");
        var save = dialog.FindControl<Button>("SaveToFileButton");
        var cancel = dialog.FindControl<Button>("CancelButton");
        var copy = dialog.FindControl<Button>("CopyAnywayButton");

        Assert.NotNull(message);
        Assert.NotNull(save);
        Assert.NotNull(cancel);
        Assert.NotNull(copy);
        Assert.Contains("clip.mp4", message.Text);
        Assert.Contains("148.5 MB", message.Text);
        Assert.Contains("100 MB", message.Text);
        Assert.Equal("Save to file", save.Content);
        Assert.Equal("Cancel", cancel.Content);
        Assert.Equal("Copy anyway", copy.Content);

        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Dialog_buttons_return_the_selected_recovery_action()
    {
        var owner = new Window();
        owner.Show();
        var cases = new[]
        {
            (ButtonName: "SaveToFileButton", Expected: ClipboardSizeLimitChoice.SaveToFile),
            (ButtonName: "CancelButton", Expected: ClipboardSizeLimitChoice.Cancel),
            (ButtonName: "CopyAnywayButton", Expected: ClipboardSizeLimitChoice.CopyAnyway),
        };

        foreach (var item in cases)
        {
            var dialog = new ClipboardSizeLimitDialog("clip.mp4", "148.5 MB", 100);
            var result = dialog.ShowDialog<ClipboardSizeLimitChoice>(owner);
            var button = dialog.FindControl<Button>(item.ButtonName);
            Assert.NotNull(button);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(item.Expected, await result);
        }

        owner.Close();
    }
}
