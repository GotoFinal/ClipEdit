using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvPreviewAudioLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Engine_rebuilds_a_two_track_mix_during_playback()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MULTI_AUDIO");
        var libraryPath = MpvNativeLibraryLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            libraryPath is null)
        {
            return;
        }

        await using var engine = await MpvPreviewEngine.CreateAsync(libraryPath);
        await engine.LoadAsync(sourcePath, CancellationToken.None);
        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -3, isMuted: false),
            new PreviewAudioTrack(streamIndex: 2, gainDb: -9, isMuted: false),
        ], CancellationToken.None);
        await engine.SetPausedAsync(false, CancellationToken.None);

        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -6, isMuted: false),
            new PreviewAudioTrack(streamIndex: 2, gainDb: -9, isMuted: true),
        ], CancellationToken.None);
        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -6, isMuted: true),
            new PreviewAudioTrack(streamIndex: 2, gainDb: -9, isMuted: true),
        ], CancellationToken.None);
        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -6, isMuted: false),
            new PreviewAudioTrack(streamIndex: 2, gainDb: -9, isMuted: false),
        ], CancellationToken.None);

        Assert.Equal(PreviewState.Playing, engine.State);
        Assert.NotNull(await engine.GetPositionAsync(CancellationToken.None));
    }
}
