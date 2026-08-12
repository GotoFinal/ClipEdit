using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Frames;

namespace ClipEdit.Media.FFmpeg.Tests.Frames;

public sealed class FfmpegFrameArgumentsTests
{
    [Fact]
    public void Arguments_keep_source_path_and_filter_as_atomic_values()
    {
        const string sourcePath = "C:\\media\\a file & whoami.mkv";

        var arguments = FfmpegFrameArguments.Create(
            sourcePath,
            3,
            new MediaTime(1001, 24_000),
            new PixelSize(1_280, 720));

        Assert.Equal(sourcePath, arguments[Array.IndexOf(arguments.ToArray(), "-i") + 1]);
        Assert.Contains("0:3", arguments);
        Assert.Contains(
            "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2",
            arguments);
        Assert.Equal("pipe:1", arguments[^1]);
    }

    [Fact]
    public void Arguments_reject_negative_timestamps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegFrameArguments.Create(
                "C:\\media\\source.mkv",
                0,
                new MediaTime(-1, 1),
                new PixelSize(1_280, 720)));
    }
}
