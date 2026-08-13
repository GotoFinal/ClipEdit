using ClipEdit.Media.Mpv.Native;

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
}
