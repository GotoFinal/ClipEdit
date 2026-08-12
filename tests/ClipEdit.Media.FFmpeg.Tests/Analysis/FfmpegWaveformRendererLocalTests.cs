using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.FFmpeg.Analysis;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Analysis;

public sealed class FfmpegWaveformRendererLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_generates_a_png_for_the_opt_in_audio_stream()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null)
        {
            return;
        }

        var streamText = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_AUDIO_STREAM");
        var streamIndex = int.TryParse(streamText, out var configuredIndex) ? configuredIndex : 1;
        var renderer = new FfmpegWaveformRenderer(ffmpegPath);

        var waveform = await renderer.RenderAsync(
            sourcePath,
            streamIndex,
            new MediaRange(MediaTime.Zero, new MediaTime(10, 1)),
            new PixelSize(800, 64));

        Assert.Equal("image/png", waveform.MediaType);
        Assert.True(waveform.EncodedImage.Length > 8);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            waveform.EncodedImage.Span[..8].ToArray());
    }
}
