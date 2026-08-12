using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvPreviewAudioLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Engine_mixes_embedded_and_external_audio_and_reuses_the_loaded_source()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var externalAudioPath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_EXTERNAL_AUDIO");
        var libraryPath = MpvNativeLibraryLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(externalAudioPath) ||
            !File.Exists(sourcePath) ||
            !File.Exists(externalAudioPath) ||
            libraryPath is null)
        {
            return;
        }

        await using var engine = await MpvPreviewEngine.CreateAsync(libraryPath);
        await engine.LoadAsync(sourcePath, CancellationToken.None);
        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -3, isMuted: false),
            new PreviewAudioTrack(
                externalAudioPath,
                streamIndex: 0,
                gainDb: -12,
                isMuted: false,
                timelineOffset: new MediaTime(1, 2)),
        ], CancellationToken.None);
        await engine.SetPausedAsync(false, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await engine.SetAudioTracksAsync(
        [
            new PreviewAudioTrack(streamIndex: 1, gainDb: -6, isMuted: false),
            new PreviewAudioTrack(
                externalAudioPath,
                streamIndex: 0,
                gainDb: -9,
                isMuted: false,
                timelineOffset: new MediaTime(1, 2)),
        ], CancellationToken.None);

        Assert.Equal(PreviewState.Playing, engine.State);
        Assert.NotNull(await engine.GetPositionAsync(CancellationToken.None));
    }

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

        await engine.SeekAsync(new MediaTime(39, 10), CancellationToken.None);
        PreviewPlaybackSnapshot snapshot = default;
        for (var attempt = 0; attempt < 30 && !snapshot.IsEndOfFile; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            snapshot = await engine.GetPlaybackSnapshotAsync(CancellationToken.None);
        }

        Assert.True(snapshot.IsEndOfFile);

        await engine.SeekAsync(MediaTime.Zero, CancellationToken.None);
        await engine.SetPausedAsync(false, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(75));
        snapshot = await engine.GetPlaybackSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.IsEndOfFile);
        Assert.True(snapshot.Position >= MediaTime.Zero);
    }
}
