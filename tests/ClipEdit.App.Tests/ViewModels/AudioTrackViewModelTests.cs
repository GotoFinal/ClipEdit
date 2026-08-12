using System.Collections.Immutable;
using ClipEdit.App.ViewModels;
using ClipEdit.Application.Media;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Probe;

namespace ClipEdit.App.Tests.ViewModels;

public sealed class AudioTrackViewModelTests
{
    [Fact]
    public void Track_has_independent_quantized_cuts_gain_and_mute()
    {
        var track = CreateTrack();
        track.SelectionStartSeconds = 2.00001;
        track.SelectionEndSeconds = 4.00001;

        Assert.True(track.RemoveSelection());
        track.GainDb = -7.25;
        track.IsMuted = true;

        Assert.Equal(new MediaTime(8, 1), track.Edit.OutputDuration);
        Assert.Equal(-7.25, track.GainDb);
        Assert.True(track.IsMuted);
        Assert.True(track.IsEdited);
    }

    [Theory]
    [InlineData(-100, -60)]
    [InlineData(20, 12)]
    public void Gain_is_bounded_to_the_supported_mixer_range(double requested, double expected)
    {
        var track = CreateTrack();

        track.GainDb = requested;

        Assert.Equal(expected, track.GainDb);
    }

    [Fact]
    public void External_track_start_is_non_negative_and_sample_quantized()
    {
        var track = CreateTrack();

        track.TimelineOffsetSeconds = 2.00001;

        Assert.Equal(new MediaTime(2, 1), track.TimelineOffset);
        Assert.True(track.IsEdited);

        track.TimelineOffsetSeconds = -3;

        Assert.Equal(MediaTime.Zero, track.TimelineOffset);
    }

    private static AudioTrackViewModel CreateTrack()
    {
        var sourcePath = Path.GetFullPath("audio.mkv");
        var audio = new AudioStreamInfo(
            1,
            "aac",
            null,
            null,
            "eng",
            "Main",
            true,
            false,
            new MediaTime(1, 48_000),
            MediaTime.Zero,
            new MediaTime(10, 1),
            48_000,
            2,
            "stereo",
            "fltp");
        var probe = new MediaProbeResult(
            sourcePath,
            "matroska",
            null,
            MediaTime.Zero,
            new MediaTime(10, 1),
            1_024,
            8_000,
            ImmutableArray.Create<MediaStreamInfo>(audio));
        return new AudioTrackViewModel(new ImportedMedia("audio.mkv", probe), audio);
    }
}
