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
    }

    [Theory]
    [InlineData("")]
    [InlineData("frame=20")]
    [InlineData("out_time_us=invalid")]
    [InlineData("out_time_us=-1")]
    public void Parser_ignores_non_time_updates(string line)
    {
        var parser = new FfmpegProgressParser();

        Assert.False(parser.Parse(line));
        Assert.Equal(TimeSpan.Zero, parser.EncodedDuration);
    }
}
