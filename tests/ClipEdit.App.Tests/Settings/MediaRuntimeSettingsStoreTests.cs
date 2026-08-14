using ClipEdit.App.Settings;

namespace ClipEdit.App.Tests.Settings;

public sealed class MediaRuntimeSettingsStoreTests
{
    [Fact]
    public void Settings_round_trip_and_normalize_optional_paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-media-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "media-tools.json");
        try
        {
            var store = new MediaRuntimeSettingsStore(path);

            Assert.Equal(MediaRuntimeSettings.Default, store.Load());
            Assert.True(store.Save(new MediaRuntimeSettings(
                true,
                "  /usr/local/bin/ffmpeg  ",
                " ",
                "/usr/local/lib/libmpv.so.2")));

            Assert.Equal(
                new MediaRuntimeSettings(
                    true,
                    "/usr/local/bin/ffmpeg",
                    null,
                    "/usr/local/lib/libmpv.so.2"),
                store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Malformed_settings_fall_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipedit-media-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json");

            Assert.Equal(MediaRuntimeSettings.Default, new MediaRuntimeSettingsStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
