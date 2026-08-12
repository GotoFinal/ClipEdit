using System.Collections.Immutable;
using ClipEdit.Domain.Geometry;
using ClipEdit.Domain.Editing;
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
    public async Task Renderer_pads_and_mixes_external_audio_that_ends_before_the_video()
    {
        var sourcePath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_MEDIA");
        var externalAudioPath = Environment.GetEnvironmentVariable("CLIPEDIT_LOCAL_EXTERNAL_AUDIO");
        var ffmpegPath = FfmpegToolLocator.FindFfmpeg();
        var ffprobePath = FfprobeExecutableLocator.Find();
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(externalAudioPath) ||
            !File.Exists(sourcePath) ||
            !File.Exists(externalAudioPath) ||
            ffmpegPath is null ||
            ffprobePath is null)
        {
            return;
        }

        var probe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(sourcePath);
        var video = probe.VideoStreams.First();
        var embeddedAudio = probe.AudioStreams.First();
        var externalProbe = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(externalAudioPath);
        var externalAudio = externalProbe.AudioStreams.First();
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"clipedit-external-audio-{Guid.NewGuid():N}.mp4");
        var plan = new ExportPlan(
            sourcePath,
            destinationPath,
            video.Index,
            audioStreamIndex: null,
            new CropRegion(video.OrientedSize, 0, 0, 320, 180),
            [new MediaRange(MediaTime.Zero, new MediaTime(3, 1))],
            new ExportPreset(
                "mp4-local-external-audio",
                "MP4 local external audio",
                ".mp4",
                ExportContainer.Mp4,
                VideoCodecFamily.H264,
                AudioCodecFamily.Aac,
                requiresEvenDimensions: true),
            audioTracks:
            [
                new ExportAudioTrackPlan(embeddedAudio.Index, -3),
                new ExportAudioTrackPlan(
                    externalAudioPath,
                    externalAudio.Index,
                    -12,
                    new MediaTime(1, 2),
                    new SourceEdit(externalProbe.Duration!.Value).Remove(
                        new MediaRange(new MediaTime(1, 4), new MediaTime(3, 4)))),
            ]);

        try
        {
            var result = await new FfmpegExportRenderer(ffmpegPath).RenderAsync(plan);
            var rendered = await new FfprobeMediaProbe(ffprobePath).ProbeAsync(result.DestinationPath);

            Assert.True(result.FileSizeBytes > 0);
            Assert.NotEmpty(rendered.AudioStreams);
            Assert.True(rendered.Duration >= new MediaTime(29, 10));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

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
