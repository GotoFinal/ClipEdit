using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ClipEdit.App.InternetMedia;
using ClipEdit.App.Views;

namespace ClipEdit.App.Tests;

public sealed class InternetMediaImportDialogTests
{
    [AvaloniaFact]
    public void Dialog_exposes_compact_quality_progress_and_download_controls()
    {
        var dialog = new InternetMediaImportDialog();

        Assert.NotNull(dialog.FindControl<ComboBox>("VideoQualityComboBox"));
        Assert.NotNull(dialog.FindControl<ComboBox>("AudioQualityComboBox"));
        Assert.NotNull(dialog.FindControl<ProgressBar>("DownloadProgressBar"));
        Assert.NotNull(dialog.FindControl<TextBlock>("StatusText"));
        Assert.NotNull(dialog.FindControl<Button>("CancelButton"));
        Assert.NotNull(dialog.FindControl<Button>("DownloadButton"));
        Assert.False(dialog.FindControl<Button>("DownloadButton")!.IsEnabled);

        dialog.Close();
    }

    [Fact]
    public void Remembered_quality_uses_the_nearest_available_cap_without_exceeding_it()
    {
        InternetVideoQualityChoice[] videoChoices =
        [
            new(null, "Best"),
            new(2160, "2160p"),
            new(1440, "1440p"),
            new(720, "720p"),
        ];
        InternetAudioQualityChoice[] audioChoices =
        [
            new(null, "Best"),
            new(256, "256 kbps"),
            new(128, "128 kbps"),
        ];

        Assert.Equal(720, InternetMediaImportDialog.SelectVideoChoice(videoChoices, 1080).MaximumHeight);
        Assert.Equal(128, InternetMediaImportDialog.SelectAudioChoice(audioChoices, 192).MaximumBitrateKbps);
        Assert.Null(InternetMediaImportDialog.SelectVideoChoice(videoChoices, null).MaximumHeight);
    }
}
