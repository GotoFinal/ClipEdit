using ClipEdit.App.Settings;

namespace ClipEdit.App.Tests.Settings;

public sealed class CanvasInteractionSettingsStoreTests
{
    [Fact]
    public void Settings_round_trip_and_normalize_untrusted_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new CanvasInteractionSettingsStore(path);

            Assert.Equal(CanvasInteractionSettings.Default, store.Load());
            Assert.True(store.Save(new CanvasInteractionSettings(500, 0, 10_000, true)));

            Assert.Equal(new CanvasInteractionSettings(50, 1, 4_096, true), store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_from_before_the_clipboard_limit_use_the_new_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "wheelZoomPercent": 12,
                  "wheelRotationDegrees": 2
                }
                """);

            Assert.Equal(
                new CanvasInteractionSettings(12, 2, 100),
                new CanvasInteractionSettingsStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_settings_fall_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json");

            Assert.Equal(
                CanvasInteractionSettings.Default,
                new CanvasInteractionSettingsStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void View_model_exposes_the_normalized_clipboard_limit_in_bytes()
    {
        using var viewModel = new ClipEdit.App.ViewModels.MainWindowViewModel(mediaProbe: null);

        viewModel.ClipboardExportMaximumMegabytes = 250;
        Assert.Equal(250L * 1_024 * 1_024, viewModel.ClipboardExportMaximumBytes);

        viewModel.ClipboardExportMaximumMegabytes = 10_000;
        Assert.Equal(4_096, viewModel.ClipboardExportMaximumMegabytes);
    }
}
