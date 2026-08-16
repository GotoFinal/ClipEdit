using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void Parser_reads_microsecond_progress_and_completion()
    {
        var parser = new FfmpegProgressParser();

        Assert.True(parser.Parse("out_time_us=1250000"));
        Assert.Equal(TimeSpan.FromMilliseconds(1_250), parser.EncodedDuration);
        Assert.True(parser.Parse("progress=end"));
        Assert.True(parser.IsComplete);
        Assert.True(parser.IsReportBoundary);
    }

    [Fact]
    public void Parser_reads_fps_speed_and_periodic_report_boundary()
    {
        var parser = new FfmpegProgressParser();

        Assert.True(parser.Parse("fps=47.75"));
        Assert.Equal(47.75, parser.FramesPerSecond);
        Assert.False(parser.IsReportBoundary);
        Assert.True(parser.Parse("speed=1.25x"));
        Assert.Equal(1.25, parser.ProcessingSpeed);
        Assert.True(parser.Parse("progress=continue"));
        Assert.True(parser.IsReportBoundary);
        Assert.False(parser.IsComplete);
    }

    [Theory]
    [InlineData("")]
    [InlineData("frame=20")]
    [InlineData("out_time_us=invalid")]
    [InlineData("out_time_us=-1")]
    [InlineData("fps=N/A")]
    [InlineData("speed=N/A")]
    public void Parser_ignores_non_time_updates(string line)
    {
        var parser = new FfmpegProgressParser();

        Assert.False(parser.Parse(line));
        Assert.Equal(TimeSpan.Zero, parser.EncodedDuration);
    }
}
