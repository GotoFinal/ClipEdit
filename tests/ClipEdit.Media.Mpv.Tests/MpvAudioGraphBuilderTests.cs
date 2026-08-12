using ClipEdit.Media.Mpv.Native;

namespace ClipEdit.Media.Mpv.Tests;

public sealed class MpvAudioGraphBuilderTests
{
    [Fact]
    public void Single_track_connects_selected_mpv_track_to_audio_output_with_gain()
    {
        var graph = MpvAudioGraphBuilder.Build([new MpvAudioGraphTrack(3, -4.5)]);

        Assert.Equal("[aid3]volume=-4.5dB[ao]", graph);
    }

    [Fact]
    public void Multiple_tracks_conform_mix_and_limit_like_export_preview_policy()
    {
        var graph = MpvAudioGraphBuilder.Build(
        [
            new MpvAudioGraphTrack(1, 0),
            new MpvAudioGraphTrack(4, 2.25),
        ]);

        Assert.Equal(
            "[aid1]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0dB[mix0];" +
            "[aid4]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=2.25dB[mix1];" +
            "[mix0][mix1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[ao]",
            graph);
    }

    [Fact]
    public void No_enabled_tracks_clears_complex_graph()
    {
        Assert.Equal(string.Empty, MpvAudioGraphBuilder.Build([]));
    }
}
