using System.Collections.Immutable;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Probe;

namespace ClipEdit.Media.FFmpeg.Tests.Probe;

public sealed class FfprobeKeyframeTests
{
    [Fact]
    public void Arguments_read_packet_timestamps_from_the_requested_stream()
    {
        var arguments = FfprobeKeyframeArguments.Create("C:\\media\\source.mp4", 2);

        Assert.Equal("2", ValueAfter(arguments, "-select_streams"));
        Assert.Contains("-show_packets", arguments);
        Assert.Equal(
            "packet=pts_time,dts_time,flags",
            ValueAfter(arguments, "-show_entries"));
        Assert.Equal("C:\\media\\source.mp4", arguments[^1]);
    }

    [Fact]
    public void Parser_normalizes_sorts_and_deduplicates_keyframe_packets()
    {
        var points = new[]
            {
                "pts_time=9.000000|dts_time=8.500000|flags=K__",
                "pts_time=5.000000|dts_time=4.500000|flags=K__",
                "pts_time=7.500000|dts_time=7.000000|flags=K__",
                "pts_time=7.500000|dts_time=N/A|flags=K__",
                "pts_time=8.000000|dts_time=7.500000|flags=___",
                "pts_time=17.000000|dts_time=16.500000|flags=K__",
            }
            .Select(line => FfprobeKeyframePacketParser.ParseLine(
                line,
                new MediaTime(5, 1),
                new MediaTime(10, 1)))
            .OfType<ClipEdit.Media.Probe.KeyframePoint>()
            .ToImmutableArray();
        var index = FfprobeKeyframePacketParser.CreateIndex(
            0,
            points);

        Assert.True(index.Timestamps.SequenceEqual(
            [MediaTime.Zero, new MediaTime(5, 2), new MediaTime(4, 1)]));
        Assert.Equal(new MediaTime(-1, 2), index.Points[0].DecodeTimestamp);
        Assert.Equal(new MediaTime(2, 1), index.Points[1].DecodeTimestamp);
        Assert.Equal(new MediaTime(7, 2), index.Points[2].DecodeTimestamp);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
