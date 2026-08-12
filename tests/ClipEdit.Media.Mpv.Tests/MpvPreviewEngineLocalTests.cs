using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvPreviewEngineLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Engine_loads_seeks_and_changes_playback_state_for_opt_in_media()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var libraryPath = MpvNativeLibraryLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            libraryPath is null)
        {
            return;
        }

        await using var engine = await MpvPreviewEngine.CreateAsync(libraryPath);

        await engine.LoadAsync(sourcePath, CancellationToken.None);
        await engine.SeekAsync(new MediaTime(1, 2), CancellationToken.None);
        await engine.SetVolumeAsync(0.25, CancellationToken.None);
        await engine.SetPausedAsync(false, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(75));

        Assert.Equal(PreviewState.Playing, engine.State);
        var position = await engine.GetPositionAsync(CancellationToken.None);
        Assert.NotNull(position);
        Assert.True(position >= MediaTime.Zero);

        await engine.SetPausedAsync(true, CancellationToken.None);
        Assert.Equal(PreviewState.Paused, engine.State);
    }
}
