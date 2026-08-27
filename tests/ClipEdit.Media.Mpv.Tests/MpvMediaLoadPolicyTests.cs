using ClipEdit.Media.Mpv.Native;
using ClipEdit.Media.Preview;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvMediaLoadPolicyTests
{
    [Fact]
    public void Source_change_clears_media_specific_audio_routing_before_load()
    {
        Assert.Equal(
        [
            ("lavfi-complex", string.Empty),
            ("aid", "no"),
        ], MpvClient.GetMediaLoadAudioResetProperties());
    }

    [Fact]
    public void Internet_source_keeps_separate_direct_video_and_audio_locations()
    {
        var source = PreviewMediaSource.Internet(
            new Uri("https://cdn.example.test/video.webm"),
            new Uri("https://cdn.example.test/audio.webm"));

        Assert.True(source.IsRemote);
        Assert.Equal("https://cdn.example.test/video.webm", source.Location);
        Assert.Equal("https://cdn.example.test/audio.webm", source.RemoteAudioLocation);
    }

    [Fact]
    public void Preview_cache_is_bounded_and_keeps_recent_data_seekable()
    {
        var options = MpvClient.GetInitializationOptions().ToDictionary(static option => option.Name, static option => option.Value);

        Assert.Equal("20", options["demuxer-readahead-secs"]);
        Assert.Equal("67108864", options["demuxer-max-bytes"]);
        Assert.Equal("33554432", options["demuxer-max-back-bytes"]);
        Assert.Equal("yes", options["demuxer-seekable-cache"]);
    }
}
