using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;
using ClipEdit.Media.FFmpeg.Probe;
using ClipEdit.Media.FFmpeg.Process;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportRendererLocalTests
{
    [Fact]
    [Trait("Category", "LocalMedia")]
    public async Task Renderer_exports_a_short_cropped_av_clip_from_the_opt_in_sample()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var audio = probe.AudioStreams.FirstOrDefault();
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-export-{Guid.NewGuid():N}.mp4");
        var preset = new ExportPreset(
            "mp4-local-test",
            "MP4 local test",
            ".mp4",
            ExportContainer.Mp4,
            VideoCodecFamily.H264,
            AudioCodecFamily.Aac,
            requiresEvenDimensions: true);
        var plan = new ExportPlan(
            sourcePath,
            destinationPath,
            video.Index,
            audio?.Index,
            new CropRegion(video.OrientedSize, 0, 0, 320, 180),
            ImmutableArray.Create(
                new MediaRange(new MediaTime(1, 1), new MediaTime(2, 1)),
                new MediaRange(new MediaTime(3, 1), new MediaTime(4, 1))),
            preset);

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);

            Assert.Equal(destinationPath, result.DestinationPath);
            Assert.True(result.FileSizeBytes > 0);
            Assert.True(File.Exists(destinationPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }
}
