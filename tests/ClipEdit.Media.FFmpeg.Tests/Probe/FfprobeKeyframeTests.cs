using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Probe;

namespace ClipEdit.Media.FFmpeg.Tests.Probe;

public sealed class FfprobeKeyframeTests
{
    [Fact]
    public void Arguments_decode_only_keyframes_from_the_requested_stream()
    {
        var arguments = FfprobeKeyframeArguments.Create("C:\\media\\source.mp4", 2);

        Assert.Equal("2", ValueAfter(arguments, "-select_streams"));
        Assert.Equal("nokey", ValueAfter(arguments, "-skip_frame"));
        Assert.Contains("-show_frames", arguments);
        Assert.Equal(
            "frame=best_effort_timestamp_time,pkt_dts_time",
            ValueAfter(arguments, "-show_entries"));
        Assert.Equal("C:\\media\\source.mp4", arguments[^1]);
    }

    [Fact]
    public void Parser_normalizes_sorts_and_deduplicates_keyframes()
    {
        const string json = """
            {
              "frames": [
                { "best_effort_timestamp_time": "9.000000" },
                { "pkt_dts_time": "5.000000" },
                { "best_effort_timestamp_time": "7.500000" },
                { "best_effort_timestamp_time": "7.500000" },
                { "best_effort_timestamp_time": "17.000000" }
              ]
            }
            """;

        var index = FfprobeKeyframeJsonParser.Parse(
            0,
            json,
            new MediaTime(5, 1),
            new MediaTime(10, 1));

        Assert.True(index.Timestamps.SequenceEqual(
            [MediaTime.Zero, new MediaTime(5, 2), new MediaTime(4, 1)]));
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
