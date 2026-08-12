using ClipEdit.Media.Mpv.Native;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;

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

    [Fact]
    public void Timeline_offset_delays_all_channels_before_gain_and_mix()
    {
        var graph = MpvAudioGraphBuilder.Build(
        [
            new MpvAudioGraphTrack(3, -4.5, new ClipEdit.Domain.Timeline.MediaTime(3, 2)),
        ]);

        Assert.Equal(
            "[aid3]adelay=delays=1.5s:all=1,volume=-4.5dB[ao]",
            graph);
    }

    [Fact]
    public void Audio_edit_mutes_removed_samples_without_rippling_timeline_time()
    {
        var edit = new SourceEdit(new MediaTime(6, 1))
            .Remove(new MediaRange(new MediaTime(2, 1), new MediaTime(4, 1)));

        var graph = MpvAudioGraphBuilder.Build(
        [
            new MpvAudioGraphTrack(3, -4.5, AudioEdit: edit),
        ]);

        Assert.Equal(
            "[aid3]aeval='if(gt(gte(t,0)*lt(t,2)+gte(t,4)*lt(t,6),0),val(ch),0)':c=same," +
            "volume=-4.5dB[ao]",
            graph);
    }
}
