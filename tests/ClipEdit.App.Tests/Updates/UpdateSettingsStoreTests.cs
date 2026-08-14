using ClipEdit.App.Updates;

namespace ClipEdit.App.Tests.Updates;

public sealed class UpdateSettingsStoreTests
{
    [Fact]
    public void Defaults_to_stable_automatic_checks_and_round_trips_beta_preference()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipedit-update-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "updates.json");
        try
        {
            var store = new UpdateSettingsStore(path);
            Assert.Equal(UpdateSettings.Default, store.Load());
            Assert.True(UpdateSettings.Default.AutomaticallyCheckForUpdates);
            Assert.False(UpdateSettings.Default.IncludeBetaVersions);

            var settings = UpdateSettings.Default with { IncludeBetaVersions = true };
            Assert.True(store.Save(settings));
            Assert.Equal(settings, store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
