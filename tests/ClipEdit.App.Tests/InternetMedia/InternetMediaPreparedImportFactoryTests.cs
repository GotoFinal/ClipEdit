using ClipEdit.App.InternetMedia;

namespace ClipEdit.App.Tests.InternetMedia;

public sealed class InternetMediaPreparedImportFactoryTests
{
    [Fact]
    public void Builds_editable_probe_for_reserved_local_copy_while_preview_remains_remote()
    {
        var info = new InternetMediaInfo(
            new Uri("https://example.test/watch/1"),
            "Remote clip",
            "Generic",
            TimeSpan.FromSeconds(90),
            [
                new InternetMediaFormat("v", "webm", "vp09.00.40.08", "none", 1920, 1080, 60, null, 1_000_000),
                new InternetMediaFormat("a", "webm", "none", "opus", null, null, null, 160, 100_000),
            ],
            [new InternetMediaChapter("Part", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))]);
        var request = new InternetMediaDownloadRequest(
            info,
            new InternetVideoQualityChoice(1080, "1080p"),
            new InternetAudioQualityChoice(160, "160 kbps"));
        var localPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clipedit-prepared", "media.mkv"));
        var prepared = new InternetMediaPreparedImport(
            request,
            localPath,
            new Uri("https://cdn.example.test/video.webm"),
            new Uri("https://cdn.example.test/audio.webm"),
            720,
            null);

        var media = InternetMediaPreparedImportFactory.Create(prepared);

        Assert.Equal("Remote clip", media.DisplayName);
        Assert.Equal(localPath, media.Probe.SourcePath);
        var video = Assert.Single(media.Probe.VideoStreams);
        Assert.Equal("vp9", video.CodecName);
        Assert.Equal(1920, video.OrientedSize.Width);
        Assert.Equal(1080, video.OrientedSize.Height);
        Assert.Equal(60, video.AverageFrameRate!.Value.FramesPerSecond);
        Assert.Equal("opus", Assert.Single(media.Probe.AudioStreams).CodecName);
        Assert.Equal("Part", Assert.Single(media.Probe.Chapters).Title);
    }

    [Fact]
    public void Requires_duration_and_dimensions_before_streaming_edit_can_start()
    {
        var info = new InternetMediaInfo(
            new Uri("https://example.test/live"),
            "Live",
            "Generic",
            null,
            [new InternetMediaFormat("live", "m3u8", "h264", "aac", 1280, 720, 30, 128, null)]);
        var request = new InternetMediaDownloadRequest(
            info,
            new InternetVideoQualityChoice(720, "720p"),
            new InternetAudioQualityChoice(128, "128 kbps"));
        var prepared = new InternetMediaPreparedImport(
            request,
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "media.mkv")),
            new Uri("https://cdn.example.test/video.mp4"),
            null,
            720,
            null);

        Assert.Throws<InternetMediaException>(() => InternetMediaPreparedImportFactory.Create(prepared));
    }
}
