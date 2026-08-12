using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Analysis;

namespace ClipEdit.Media.FFmpeg.Tests.Analysis;

public sealed class FfmpegWaveformArgumentsTests
{
    [Fact]
    public void Creates_a_bounded_visible_range_waveform_job_for_the_exact_stream()
    {
        var arguments = FfmpegWaveformArguments.Create(
            @"C:\media files\source.mkv",
            3,
            new MediaRange(new MediaTime(25, 2), new MediaTime(75, 2)),
            new PixelSize(1_600, 72));

        Assert.Contains("12.5", arguments);
        Assert.Contains("25", arguments);
        Assert.Contains(@"C:\media files\source.mkv", arguments);
        Assert.Contains(
            "[0:3]aformat=channel_layouts=mono," +
            "showwavespic=s=1600x72:colors=0x9D83FF:scale=sqrt," +
            "format=rgba[waveform]",
            arguments);
        Assert.Equal("pipe:1", arguments[^1]);
    }

    [Fact]
    public void Rejects_an_empty_visible_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegWaveformArguments.Create(
                "source.mkv",
                0,
                new MediaRange(MediaTime.Zero, MediaTime.Zero),
                new PixelSize(800, 48)));
    }
}
