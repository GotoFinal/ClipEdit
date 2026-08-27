using ClipEdit.App.Settings;

namespace ClipEdit.App.Tests.Settings;

public sealed class InternetMediaSettingsStoreTests
{
    [Fact]
    public void Round_trips_remembered_quality_and_parallelism()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-internet-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new InternetMediaSettingsStore(Path.Combine(directory, "internet-media.json"));
            var expected = new InternetMediaSettings(1080, 192, 8, 480);

            Assert.True(store.Save(expected));
            Assert.Equal(expected, store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Invalid_values_return_to_safe_defaults()
    {
        var normalized = new InternetMediaSettings(20_000, 2, 500, 50_000).Normalize();

        Assert.Null(normalized.PreferredVideoHeight);
        Assert.Null(normalized.PreferredAudioBitrateKbps);
        Assert.Equal(16, normalized.ConcurrentFragments);
        Assert.Equal(InternetMediaSettings.DefaultPreviewVideoHeight, normalized.PreviewVideoHeight);
    }
}
