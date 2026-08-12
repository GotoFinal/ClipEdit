using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Frames;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Frames;

public sealed class FfmpegFrameDecoderLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Decoder_extracts_a_png_from_the_opt_in_sample()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null)
        {
            return;
        }

        var decoder = new FfmpegFrameDecoder(ffmpegPath);

        var frame = await decoder.DecodeAsync(
            sourcePath,
            0,
            new MediaTime(1, 1),
            new PixelSize(1_280, 720));

        Assert.Equal("image/png", frame.MediaType);
        Assert.True(frame.EncodedImage.Length > 8);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            frame.EncodedImage.Span[..8].ToArray());
    }
}
