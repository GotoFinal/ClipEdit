using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Timeline;
using ClipEdit.Media.Export;
using ClipEdit.Media.FFmpeg.Export;

namespace ClipEdit.Media.FFmpeg.Tests.Export;

public sealed class FfmpegExportRendererTests
{
    [Fact]
    public async Task Existing_destination_is_rejected_before_launch_and_remains_unchanged()
    {
        var sourcePath = Path.GetTempFileName();
        var destinationPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(destinationPath, "keep me");

        try
        {
            var renderer = new FfmpegExportRenderer(Environment.ProcessPath!);

            var exception = await Assert.ThrowsAsync<ExportException>(() =>
                renderer.RenderAsync(CreatePlan(sourcePath, destinationPath)));

            Assert.Equal(ExportFailure.DestinationExists, exception.Failure);
            Assert.Equal("keep me", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task Source_can_never_be_used_as_its_own_destination()
    {
        var sourcePath = Path.GetTempFileName();

        try
        {
            var renderer = new FfmpegExportRenderer(Environment.ProcessPath!);

            var exception = await Assert.ThrowsAsync<ExportException>(() =>
                renderer.RenderAsync(CreatePlan(sourcePath, sourcePath)));

            Assert.Equal(ExportFailure.DestinationUnavailable, exception.Failure);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static ExportPlan CreatePlan(string sourcePath, string destinationPath)
    {
        var preset = new ExportPreset(
            "mp4",
            "MP4",
            ".mp4",
            ExportContainer.Mp4,
            VideoCodecFamily.H264,
            AudioCodecFamily.Aac,
            requiresEvenDimensions: true);
        return new ExportPlan(
            sourcePath,
            destinationPath,
            0,
            null,
            CropRegion.FullFrame(new PixelSize(100, 100)),
            ImmutableArray.Create(new MediaRange(MediaTime.Zero, new MediaTime(1, 1))),
            preset);
    }
}
