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
            Assert.True(store.Save(new CanvasInteractionSettings(500, 0)));

            Assert.Equal(new CanvasInteractionSettings(50, 1), store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
}
