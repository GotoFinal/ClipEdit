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

    [Fact]
    public void Hdr_preview_is_tone_mapped_to_full_range_srgb_before_png_encoding()
    {
        var arguments = FfmpegFrameArguments.Create(
            "C:\\media\\hdr.mp4",
            0,
            MediaTime.Zero,
            new PixelSize(640, 360),
            toneMapHdr: true);

        var filter = arguments[Array.IndexOf(arguments.ToArray(), "-vf") + 1];

        Assert.Contains("zscale=transfer=linear:npl=100", filter);
        Assert.Contains("tonemap=mobius:desat=0", filter);
        Assert.Contains("zscale=transfer=iec61966-2-1:matrix=gbr:range=pc", filter);
        Assert.EndsWith(
            "scale=640:360:force_original_aspect_ratio=decrease:force_divisible_by=2",
            filter,
            StringComparison.Ordinal);
    }
}
